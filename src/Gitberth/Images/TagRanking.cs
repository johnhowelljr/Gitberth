using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Gitberth.Images;

/// <summary>
/// Ranks a registry's tags for the tag picker, newest stable first. Signatures, commit and CI tags, single-arch copies and
/// tags for another OS are hidden; each tag is read as a version (1.31.2, 3.16.0a2, 2025.1.1, a date) or a channel (latest,
/// stable, bookworm) plus a variant family (alpine, trixie-slim); the tags that name one image (one digest, or without
/// digests one version and its shorter prefixes) are one row; then the primary scheme, the default family, the variant
/// chips, the recommended row and the tag to preselect are worked out. Pure: the host's platform is given, nothing is asked.
/// </summary>
internal static partial class TagRanking
{
    /// <summary>How a version counts: semantic (1.31.2), calendar (2025.1.1), a date (20260805, RELEASE.…), a build number (11079).</summary>
    internal enum VersionScheme { Semver, Calver, Date, Build }

    /// <summary>A tag as the ranking reads it.</summary>
    /// <param name="Prefix">"", "v", "version-" (LinuxServer's moving tag), or "RELEASE." (MinIO's dates).</param>
    /// <param name="Nums">The version's numbers; empty for a channel.</param>
    /// <param name="Stage">A pre-release's stage (rc, beta, a…), lower case; null for a release.</param>
    /// <param name="StageRank">dev and the like 0, alpha 1, beta 2, pre 3, rc 4.</param>
    /// <param name="Family">The variant: the tokens that aren't the version, its stage, its build or a commit, joined by "-".</param>
    /// <param name="Channel">A channel's name (latest, stable, bookworm), lower case; null for a version.</param>
    /// <param name="Version">The version as written, without its prefix, family or commit (1.31.2, 3.16.0a2, 1.43.5.11079-ls333).</param>
    internal sealed record ParsedTag(string Name, string Prefix, long[] Nums, VersionScheme Scheme, string? Stage, int StageRank, long[] StageNums,
        long? Build, string Family, string? Channel, bool Unstable, string? Version)
    {
        public bool IsVersion => Nums.Length > 0;
        public bool StableVersion => IsVersion && !Unstable;
        public int Parts => Nums.Length;
    }

    /// <summary>At most this many variant chips.</summary>
    internal const int MaxFamilies = 12;

    /// <summary>The plain family's chip.</summary>
    internal const string StandardLabel = "Standard";

    // Channels that move ahead of releases: a row of them alone is a pre-release.
    private static readonly HashSet<string> UnstableChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "edge", "nightly", "dev", "develop", "main", "master", "trunk", "beta", "alpha", "rc", "next", "canary", "unstable", "preview",
        "insiders", "snapshot", "test", "testing", "experimental",
    };

    /// <summary>
    /// The tags as the picker lists them. <paramref name="total"/> is the registry's count of tags (Docker Hub's, or all a
    /// tag list gave), <paramref name="more"/> whether older pages exist.
    /// </summary>
    internal static TagList Rank(string image, string registry, IReadOnlyList<TagFacts> tags, HostPlatform host, int total, bool more)
    {
        var names = new HashSet<string>(tags.Select(t => t.Name), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Pages read a moment apart can repeat a tag: it counts once, as it was first read.
        var shown = tags.Where(t => TagName().IsMatch(t.Name) && seen.Add(t.Name) && !Hidden(t, names, host)).ToList();
        var families = FamilyTokens(shown.Select(t => t.Name));
        var parsed = shown.Select(t => (Tag: Parse(t.Name, families), Facts: t)).ToList();
        var digests = parsed.Any(p => p.Facts.Digest is not null);
        var rows = digests ? ByDigest(parsed) : Inferred(parsed);

        // The primary scheme comes from latest's canonical member, which the scheme itself helps choose: read it without first.
        var latest = rows.FirstOrDefault(r => r.Members.Any(m => m.Tag.Name == "latest"));
        foreach (var row in rows) row.Canonical = Canonical(row.Members.Select(m => m.Tag), null);
        var primary = latest is { Canonical.IsVersion: true } ? latest.Canonical.Scheme
            : rows.Where(r => r.Canonical.StableVersion).GroupBy(r => r.Canonical.Scheme)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (VersionScheme?)g.Key).FirstOrDefault() ?? VersionScheme.Semver;
        foreach (var row in rows)
        {
            row.Canonical = Canonical(row.Members.Select(m => m.Tag), primary);
            row.PreRelease = row.Members.Any(m => m.Tag.Unstable) && !row.Members.Any(m => m.Tag.StableVersion && m.Tag.Scheme == primary);
            row.Fits = row.Platforms.Count == 0 || row.Platforms.Any(p => Fits(p, host));
        }

        var versions = rows.Where(r => r.Canonical.IsVersion && r.Canonical.Scheme == primary).ToList();
        var stable = versions.Where(r => !r.PreRelease).ToList();
        var defaultFamily = latest?.Canonical.Family
            ?? (stable.Any(r => r.Families.Contains("")) ? ""
                : stable.SelectMany(r => r.Families).GroupBy(f => f, StringComparer.Ordinal).OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? "");
        versions.Sort((a, b) => Newer(b, a, defaultFamily));
        var other = rows.Except(versions).OrderBy(r => r.Pushed is null).ThenByDescending(r => r.Pushed)
            .ThenBy(r => r.Canonical.Name, StringComparer.Ordinal).ToList();

        // latest's row when it names a stable image for this host; without digests latest names nothing known.
        var recommended = (digests && latest is { PreRelease: false, Fits: true } ? latest : null)
            ?? versions.FirstOrDefault(r => !r.PreRelease && r.Fits && r.Families.Contains(defaultFamily))
            ?? latest;
        var built = rows.ToDictionary(r => r, Build);
        string? preselect;
        if (recommended is null) preselect = shown.Any(t => t.Name == "latest") ? "latest" : null;
        else
        {
            var (label, follows) = View(recommended.Members.Select(m => m.Tag).ToList(), defaultFamily, recommended.Canonical);
            preselect = follows.FirstOrDefault(f => f.StableVersion && label.IsVersion && f.Scheme == label.Scheme && f.Parts == label.Parts - 1
                && f.Nums.AsSpan().SequenceEqual(label.Nums.AsSpan(0, f.Parts)))?.Name ?? label.Name;
        }
        return new TagList(image, registry, shown.Any(t => t.Pushed is not null), Chips(stable, defaultFamily),
            versions.Select(r => built[r]).ToList(), other.Select(r => built[r]).ToList(), recommended is null ? null : built[recommended], preselect,
            shown.Count, Math.Max(total, tags.Count), more);
    }

    /// <summary>
    /// A row under one family's chip: its member there with the most parts (shorter on ties; a version in its canonical
    /// member's scheme first), and that family's other members, the tags that follow it: versions by parts (longest first),
    /// then channels (latest, stable, lts, current, mainline, then a-z), then the same version under another prefix (v3.3.1
    /// for 3.3.1). A row with no member in the family is shown as its canonical member's family.
    /// </summary>
    internal static (ParsedTag Label, List<ParsedTag> Follows) View(IReadOnlyList<ParsedTag> members, string family, ParsedTag canonical)
    {
        var there = members.Where(m => m.Family == family).ToList();
        if (there.Count == 0) there = members.Where(m => m.Family == canonical.Family).ToList();
        if (there.Count == 0) there = [canonical];
        var label = there.OrderBy(m => m.IsVersion && m.Scheme == canonical.Scheme ? 0 : 1).ThenByDescending(m => m.Parts)
            .ThenBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal).First();
        var others = there.Where(m => !ReferenceEquals(m, label)).ToList();
        var follows = others.Where(m => m.IsVersion && !SameVersion(m, label)).OrderByDescending(m => m.Parts).ThenBy(PrefixRank)
            .ThenBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Concat(others.Where(m => !m.IsVersion).OrderBy(ChannelRank).ThenBy(m => m.Name, StringComparer.Ordinal))
            .Concat(others.Where(m => m.IsVersion && SameVersion(m, label)).OrderBy(PrefixRank).ThenBy(m => m.Name, StringComparer.Ordinal))
            .ToList();
        return (label, follows);
    }

    private static readonly ConditionalWeakTable<TagList, Dictionary<string, ParsedTag>> ReadLists = new();

    /// <summary>Every tag a list's rows hold, read as the ranking read them (once per list).</summary>
    internal static Dictionary<string, ParsedTag> TagsOf(TagList list) => ReadLists.GetValue(list, l =>
    {
        var names = l.Versions.Concat(l.Other).SelectMany(r => r.Also.Prepend(r.Tag)).Distinct(StringComparer.Ordinal).ToList();
        var families = FamilyTokens(names);
        return names.ToDictionary(n => n, n => Parse(n, families), StringComparer.Ordinal);
    });

    /// <summary>A tag Docker takes, as ImageReference reads one: up to 128 letters, digits, _ . and -, not starting with . or -.</summary>
    internal static bool IsTagName(string name) => TagName().IsMatch(name);

    /// <summary>The family tokens the tags use (alpine of 1.31.2-alpine, slim and bookworm of 3.12-slim-bookworm), for reading a channel.</summary>
    internal static IReadOnlySet<string> FamilyTokens(IEnumerable<string> names)
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            foreach (var token in Parse(name, none).Family.Split('-', StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(token);
        return tokens;
    }

    /// <summary>
    /// A tag read as a version or a channel, with its family. A channel whose first word is a family elsewhere (alpine,
    /// trixie, slim-bookworm) is that family's latest.
    /// </summary>
    internal static ParsedTag Parse(string name, IReadOnlySet<string> families)
    {
        if (Release().Match(name) is { Success: true } release)
        {
            var date = new long[6];
            for (var i = 0; i < 6; i++) date[i] = long.Parse(release.Groups[i + 1].ValueSpan, CultureInfo.InvariantCulture);
            var rest = release.Groups[7].Success ? release.Groups[7].Value : "";
            var written = name["RELEASE.".Length..(name.Length - (rest.Length == 0 ? 0 : rest.Length + 1))];
            return new ParsedTag(name, "RELEASE.", date, VersionScheme.Date, null, 0, [], null, rest, null, false, written);
        }
        var prefix = "";
        var text = name;
        if (text.Length > 8 && text.StartsWith("version-", StringComparison.OrdinalIgnoreCase)) (prefix, text) = (text[..8], text[8..]);
        var tokens = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) tokens = [text];
        long[] nums = [];
        var dated = false;
        string? stage = null;
        var stageRank = 0;
        long[] stageNums = [];
        long? build = null;
        string? channel = null;
        var family = new List<string>();
        var version = new List<string>();
        var at = 1;
        if (DateHead(tokens) is { } head)
        {
            (nums, dated, at) = (head.Nums, true, head.Used);
            version.Add(string.Join('-', tokens[..head.Used]));
        }
        else if (VersionHead().Match(tokens[0]) is { Success: true } v && Numbers(v.Groups[2].Value) is { } numbers)
        {
            nums = numbers;
            prefix += v.Groups[1].Value;
            var tail = v.Groups[3].Value;
            if (tail.Length > 0 && Stage(tail, glued: true) is { } glued)
            {
                (stage, stageRank, stageNums) = glued;
                version.Add(tokens[0][v.Groups[1].Length..]);
            }
            else
            {
                if (tail.Length > 0) family.Add(tail);
                version.Add(v.Groups[2].Value);
            }
        }
        else if (!tokens[0].Equals("latest", StringComparison.OrdinalIgnoreCase) && families.Contains(tokens[0]))
        {
            channel = "latest";
            family.Add(tokens[0]);
        }
        else channel = tokens[0].ToLowerInvariant();

        for (; at < tokens.Length; at++)
        {
            var token = tokens[at];
            if (stage is null && Stage(token, glued: false) is { } s)
            {
                (stage, stageRank, stageNums) = s;
                if (nums.Length > 0) version.Add(token);
                continue;
            }
            if (build is null && BuildToken().Match(token) is { Success: true } b
                && long.TryParse(b.Groups[1].Success ? b.Groups[1].Value : b.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                build = number;
                if (nums.Length > 0) version.Add(token);
                continue;
            }
            // A commit hash (LinuxServer's b3b13e506) says nothing the version doesn't.
            if (HexToken().IsMatch(token) && token.Any(c => c is >= 'a' and <= 'f')) continue;
            family.Add(token);
        }
        var unstable = stage is not null || channel is not null && UnstableChannels.Contains(channel);
        var scheme = dated ? VersionScheme.Date
            : nums.Length >= 2 && nums[0] is >= 1900 and <= 2199 ? VersionScheme.Calver
            : nums.Length == 1 && nums[0] >= 1000 ? VersionScheme.Build
            : VersionScheme.Semver;
        return new ParsedTag(name, prefix, nums, scheme, stage, stageRank, stageNums, build, string.Join('-', family), channel, unstable,
            nums.Length > 0 ? string.Join('-', version) : null);
    }

    /// <summary>
    /// Whether the tag stays out of the list (it can still be typed): a signature or attestation, a commit or CI tag, a copy
    /// of another tag for one architecture (amd64-latest), or one for another OS (by its platforms, else Windows by its name).
    /// Another architecture of the host's OS stays: it's marked instead.
    /// </summary>
    internal static bool Hidden(TagFacts tag, IReadOnlySet<string> names, HostPlatform host)
    {
        var name = tag.Name;
        if (Signature().IsMatch(name) || name.EndsWith(".sig", StringComparison.Ordinal) || name.EndsWith(".att", StringComparison.Ordinal)
            || name.EndsWith(".sbom", StringComparison.Ordinal)) return true;
        // All digits is a date or a build, never a commit.
        if (CommitTag().Match(name) is { Success: true } commit && commit.Groups[1].Value.Any(c => c is >= 'a' and <= 'f')) return true;
        if (ShaTag().IsMatch(name) || PullRequestTag().IsMatch(name) || CacheTag().IsMatch(name)) return true;
        if (ArchCopy().Match(name) is { Success: true } copy && names.Contains(copy.Groups[1].Value)) return true;
        if (tag.Platforms is { Count: > 0 } platforms) return !platforms.Any(p => OsOf(p).Equals(host.Os, StringComparison.OrdinalIgnoreCase));
        return host.Os == "linux" && name.Split('-').Any(t => t.StartsWith("windowsservercore", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("nanoserver", StringComparison.OrdinalIgnoreCase) || t.Equals("windows", StringComparison.OrdinalIgnoreCase) || Ltsc().IsMatch(t));
    }

    /// <summary>
    /// Whether an image for <paramref name="platform"/> (linux/arm64/v8) runs on the host: its OS and architecture, and a
    /// variant that's the host's, none, or (32-bit arm) an older one.
    /// </summary>
    internal static bool Fits(string platform, HostPlatform host)
    {
        var parts = platform.Split('/');
        if (parts.Length < 2 || !parts[0].Equals(host.Os, StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals(host.Architecture, StringComparison.OrdinalIgnoreCase)) return false;
        var variant = parts.Length > 2 ? parts[2] : "";
        if (host.Variant is null || variant.Length == 0 || variant.Equals(host.Variant, StringComparison.OrdinalIgnoreCase)) return true;
        return parts[1] == "arm" && ArmLevel(variant) is int level && ArmLevel(host.Variant) is int hostLevel && level <= hostLevel;
    }

    private static int? ArmLevel(string variant) =>
        variant.Length > 1 && variant[0] is 'v' or 'V' && int.TryParse(variant.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : null;

    private static string OsOf(string platform) => platform.Split('/')[0];

    // ---- rows ----

    /// <summary>The tags naming one image.</summary>
    private sealed class Row
    {
        public List<(ParsedTag Tag, TagFacts Facts)> Members { get; } = [];
        public string? Digest { get; init; }
        public bool Inferred { get; set; }
        public ParsedTag Canonical { get; set; } = null!;
        public bool PreRelease { get; set; }
        public bool Fits { get; set; }

        public DateTime? Pushed => Members.Max(m => m.Facts.Pushed);

        public IReadOnlyList<string> Platforms => Members.SelectMany(m => m.Facts.Platforms ?? []).Distinct(StringComparer.Ordinal).ToList();

        public HashSet<string> Families => Members.Select(m => m.Tag.Family).ToHashSet(StringComparer.Ordinal);
    }

    private static List<Row> ByDigest(List<(ParsedTag Tag, TagFacts Facts)> tags)
    {
        var rows = new List<Row>();
        var byDigest = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            Row? row;
            if (tag.Facts.Digest is not { } digest)
            {
                row = new Row();
                rows.Add(row);
            }
            else if (!byDigest.TryGetValue(digest, out row))
            {
                row = new Row { Digest = digest };
                rows.Add(row);
                byDigest[digest] = row;
            }
            row.Members.Add(tag);
        }
        return rows;
    }

    // Without digests, a stable version is the newest stable one it's a prefix of (1.27 and 1 of 1.27.3), with the same
    // prefix, family and scheme: inferred. Channels stay rows of their own.
    private static List<Row> Inferred(List<(ParsedTag Tag, TagFacts Facts)> tags)
    {
        static string Key(ParsedTag t, int parts) =>
            $"{t.Prefix}\u0001{t.Family}\u0001{t.Scheme}\u0001{string.Join('.', t.Nums.Take(parts))}";
        var newest = new Dictionary<string, (ParsedTag Tag, TagFacts Facts)>(StringComparer.Ordinal);
        foreach (var tag in tags.Where(t => t.Tag.StableVersion))
            for (var parts = 1; parts < tag.Tag.Parts; parts++)
            {
                var key = Key(tag.Tag, parts);
                if (!newest.TryGetValue(key, out var known) || Compare(tag.Tag, known.Tag) > 0) newest[key] = tag;
            }
        var rows = new List<Row>();
        var rowOf = new Dictionary<string, Row>(StringComparer.Ordinal);
        var aliases = new List<((ParsedTag Tag, TagFacts Facts) Tag, string Of)>();
        foreach (var tag in tags)
        {
            if (tag.Tag.StableVersion && newest.TryGetValue(Key(tag.Tag, tag.Tag.Parts), out var of))
            {
                aliases.Add((tag, of.Tag.Name));
                continue;
            }
            var row = new Row();
            row.Members.Add(tag);
            rows.Add(row);
            rowOf[tag.Tag.Name] = row;
        }
        foreach (var (tag, of) in aliases)
        {
            var row = rowOf[of];
            row.Members.Add(tag);
            row.Inferred = true;
        }
        return rows;
    }

    // The member a row is shown by: a stable version, then a pre-release, then a channel; the primary scheme; the most
    // parts; one with a build; no prefix, then v; the shortest; then by its characters.
    private static ParsedTag Canonical(IEnumerable<ParsedTag> members, VersionScheme? primary) =>
        members.OrderBy(m => m.StableVersion ? 0 : m.IsVersion ? 1 : 2)
            .ThenBy(m => primary is null || m.IsVersion && m.Scheme == primary ? 0 : 1)
            .ThenByDescending(m => m.Parts)
            .ThenBy(m => m.Build is null ? 1 : 0)
            .ThenBy(PrefixRank)
            .ThenBy(m => m.Name.Length)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .First();

    // Above zero when a goes first: the newer version, then (as All shows them) the default family before the others, the
    // newest push, the name.
    private static int Newer(Row a, Row b, string defaultFamily)
    {
        var c = Compare(a.Canonical, b.Canonical);
        if (c != 0) return c;
        c = (a.Canonical.Family == defaultFamily).CompareTo(b.Canonical.Family == defaultFamily);
        if (c != 0) return c;
        c = string.CompareOrdinal(b.Canonical.Family, a.Canonical.Family);
        if (c != 0) return c;
        c = (a.Pushed ?? DateTime.MinValue).CompareTo(b.Pushed ?? DateTime.MinValue);
        if (c != 0) return c;
        return string.CompareOrdinal(b.Canonical.Name, a.Canonical.Name);
    }

    /// <summary>
    /// Above zero when <paramref name="a"/> is the newer version: its numbers (a missing part counts as −1, so 1.27.3 is above
    /// 1.27), a release before a pre-release, the stage (dev, alpha, beta, pre, rc) and its numbers (rc.10 above rc.2), the build.
    /// </summary>
    internal static int Compare(ParsedTag a, ParsedTag b)
    {
        var c = Numbers(a.Nums, b.Nums);
        if (c != 0) return c;
        c = (a.Stage is null).CompareTo(b.Stage is null);
        if (c != 0) return c;
        if (a.Stage is not null && b.Stage is not null)
        {
            c = a.StageRank.CompareTo(b.StageRank);
            if (c != 0) return c;
            c = Numbers(a.StageNums, b.StageNums);
            if (c != 0) return c;
        }
        return (a.Build ?? -1).CompareTo(b.Build ?? -1);
    }

    private static int Numbers(long[] a, long[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : -1).CompareTo(i < b.Length ? b[i] : -1);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool SameVersion(ParsedTag a, ParsedTag b) =>
        a.IsVersion && b.IsVersion && a.Nums.AsSpan().SequenceEqual(b.Nums) && a.Stage == b.Stage && a.StageNums.AsSpan().SequenceEqual(b.StageNums)
        && a.Build == b.Build;

    private static int PrefixRank(ParsedTag t) => t.Prefix.Length == 0 ? 0 : t.Prefix is "v" or "V" ? 1 : 2;

    private static int ChannelRank(ParsedTag t) => t.Channel switch
    {
        "latest" => 0,
        "stable" => 1,
        "lts" => 2,
        "current" => 3,
        "mainline" => 4,
        _ => 5,
    };

    private static TagRow Build(Row row)
    {
        var canonical = row.Canonical;
        var others = row.Members.Select(m => m.Tag).Where(m => !ReferenceEquals(m, canonical)).ToList();
        var also = others.Where(m => m.IsVersion).OrderByDescending(m => m.Parts).ThenBy(m => m.Family == canonical.Family ? 0 : 1)
            .ThenBy(PrefixRank).ThenBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Concat(others.Where(m => !m.IsVersion).OrderBy(ChannelRank).ThenBy(m => m.Name, StringComparer.Ordinal))
            .Select(m => m.Name).ToList();
        var families = row.Families.OrderBy(f => f == canonical.Family ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal).ToList();
        var size = row.Members.FirstOrDefault(m => ReferenceEquals(m.Tag, canonical)).Facts?.Size ?? row.Members.Select(m => m.Facts.Size).FirstOrDefault(s => s is not null);
        return new TagRow(canonical.Name, canonical.Version, also, row.Inferred, families, row.Digest, row.Pushed, size, row.Platforms, row.PreRelease, row.Fits);
    }

    // The chips: families with a stable row, less one whose rows all sit in a shorter family's (alpine3.24's in alpine's);
    // the default first, then by rows, at most 12.
    private static List<TagFamily> Chips(List<Row> stable, string defaultFamily)
    {
        var rowsOf = new Dictionary<string, HashSet<Row>>(StringComparer.Ordinal);
        foreach (var row in stable)
            foreach (var family in row.Families)
            {
                if (!rowsOf.TryGetValue(family, out var set)) rowsOf[family] = set = [];
                set.Add(row);
            }
        var kept = new List<string>();
        var keptSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in rowsOf.Keys.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal))
        {
            // A shorter family that holds all its rows holds its first one: only that row's families can (linear in the
            // families, where every kept one against every other would be quadratic: a registry may list 10,000).
            var rows = rowsOf[family];
            if (family != defaultFamily && rows.First().Families.Any(k => k.Length < family.Length && keptSet.Contains(k) && rows.IsSubsetOf(rowsOf[k]))) continue;
            kept.Add(family);
            keptSet.Add(family);
        }
        return kept.OrderBy(f => f == defaultFamily ? 0 : 1).ThenByDescending(f => rowsOf[f].Count).ThenBy(f => f, StringComparer.Ordinal)
            .Take(MaxFamilies)
            .Select(f => new TagFamily(f, f.Length == 0 ? StandardLabel : f, rowsOf[f].Count, f == defaultFamily))
            .ToList();
    }

    // ---- reading ----

    private static (long[] Nums, int Used)? DateHead(string[] tokens)
    {
        var first = tokens[0];
        if (DateToken().IsMatch(first))
            return ([long.Parse(first.AsSpan(0, 4), CultureInfo.InvariantCulture), long.Parse(first.AsSpan(4, 2), CultureInfo.InvariantCulture),
                long.Parse(first.AsSpan(6, 2), CultureInfo.InvariantCulture)], 1);
        if (tokens.Length >= 3 && Year().IsMatch(first) && Month().IsMatch(tokens[1]) && Day().IsMatch(tokens[2]))
            return ([long.Parse(first, CultureInfo.InvariantCulture), long.Parse(tokens[1], CultureInfo.InvariantCulture), long.Parse(tokens[2], CultureInfo.InvariantCulture)], 3);
        return null;
    }

    private static long[]? Numbers(string dotted)
    {
        var parts = dotted.Split('.');
        var nums = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        return nums;
    }

    // A pre-release stage: a word (rc, beta.3, alpha.0.1.1) as a token of its own, or glued to the version (3.16.0a2, 18beta1),
    // where a, b and c count too, with a number.
    private static (string Stage, int Rank, long[] Nums)? Stage(string text, bool glued)
    {
        var m = (glued ? GluedStage() : StageToken()).Match(text);
        if (!m.Success) return null;
        var word = m.Groups[1].Value.ToLowerInvariant();
        var digits = m.Groups[2].Value;
        if (word.Length == 1 && digits.Length == 0) return null;
        long[] nums = [];
        if (digits.Length > 0)
        {
            if (Numbers(digits) is not { } read) return null;
            nums = read;
        }
        var rank = word switch
        {
            "alpha" or "a" => 1,
            "beta" or "b" => 2,
            "pre" or "preview" => 3,
            "rc" or "c" => 4,
            _ => 0,
        };
        return (word, rank, nums);
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagName();

    [GeneratedRegex(@"^RELEASE\.(\d{4})-(\d\d)-(\d\d)T(\d\d)-(\d\d)-(\d\d)Z(?:-(.+))?$")]
    private static partial Regex Release();

    [GeneratedRegex(@"^([vV]?)(\d+(?:\.\d+)*)([A-Za-z]+\.?\d*(?:\.\d+)*)?$")]
    private static partial Regex VersionHead();

    [GeneratedRegex(@"^(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])$")]
    private static partial Regex DateToken();

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex Year();

    [GeneratedRegex("^(?:0[1-9]|1[0-2])$")]
    private static partial Regex Month();

    [GeneratedRegex(@"^(?:0[1-9]|[12]\d|3[01])$")]
    private static partial Regex Day();

    [GeneratedRegex(@"^(alpha|beta|preview|pre|rc|dev|nightly|canary|snapshot|edge|insiders|next|unstable|experimental)(?:\.?(\d+(?:\.\d+)*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex StageToken();

    [GeneratedRegex(@"^(alpha|beta|preview|pre|rc|dev|nightly|canary|snapshot|edge|insiders|next|unstable|experimental|a|b|c)(?:\.?(\d+(?:\.\d+)*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex GluedStage();

    [GeneratedRegex(@"^(?:(?:ls|r|build\.?)(\d+)|(\d+))$")]
    private static partial Regex BuildToken();

    [GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial Regex HexToken();

    [GeneratedRegex(@"^sha256-[0-9a-f]{64}(?:\.(?:sig|att|sbom))?$")]
    private static partial Regex Signature();

    [GeneratedRegex("^(?:sha-)?([0-9a-f]{7,40})$")]
    private static partial Regex CommitTag();

    [GeneratedRegex("^sha-[0-9a-f]+$")]
    private static partial Regex ShaTag();

    [GeneratedRegex(@"^(?:pr|mr)-\d+$")]
    private static partial Regex PullRequestTag();

    [GeneratedRegex("^(?:build)?cache(?:-.*)?$")]
    private static partial Regex CacheTag();

    [GeneratedRegex("^(?:amd64|x86_64|arm64v8|arm64|aarch64|arm32v[5-7]|armv[5-7]|armhf|i386|386|ppc64le|s390x|riscv64)-(.+)$")]
    private static partial Regex ArchCopy();

    [GeneratedRegex(@"^ltsc\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex Ltsc();
}
