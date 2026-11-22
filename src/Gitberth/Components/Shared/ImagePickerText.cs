using System.Globalization;
using Gitberth.Components.NewApp;
using Gitberth.Images;

namespace Gitberth.Components.Shared;

/// <summary>
/// New app's image search and the tag picker (New app, Settings › Image's Choose tag…), in words: counts as Docker Hub
/// writes them, a badge's word, when a result was updated, the line for a Docker Hub that won't answer with its countdown,
/// what a chosen tag follows, and the small choices behind the keyboard and the filter. Pure, so they're tested
/// (ImagePickerTextTests); the components only lay them out.
/// </summary>
public static class ImagePickerText
{
    /// <summary>The search box asks Docker Hub from this many characters, and up to <see cref="SearchMax"/>.</summary>
    public const int SearchMin = 2;

    public const int SearchMax = 100;

    /// <summary>What the basic search (Docker Hub's v1, when its full one failed) is said with.</summary>
    public const string BasicLine = "Docker Hub's full search isn't answering: this is its basic one, with official images marked only.";

    /// <summary>
    /// Whether the search box's text is searched on Docker Hub: 2 to 100 characters, and not a reference (no ':' or '@',
    /// and no dot in its first '/' part: ghcr.io/…). A reference is listed as typed instead (<see cref="Pasted"/>).
    /// </summary>
    public static bool Searchable(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length is < SearchMin or > SearchMax || t.Contains(':') || t.Contains('@')) return false;
        var slash = t.IndexOf('/');
        return slash < 0 || !t[..slash].Contains('.');
    }

    /// <summary>
    /// The reference typed or pasted in the search box, listed first and kept as typed (any registry, a port, a tag, a
    /// digest); null when the text is a search: no ':' or '@', and no dot in its first '/' part.
    /// </summary>
    public static ImageReference? Pasted(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return null;
        var slash = t.IndexOf('/');
        var first = slash < 0 ? "" : t[..slash];
        var reference = t.Contains(':') || t.Contains('@') || first.Contains('.') || first == "localhost";
        return reference ? ImageReference.Parse(t) : null;
    }

    /// <summary>A count as Docker Hub's pages write one: 842, 21.4k, 31M, 13.6B.</summary>
    public static string Count(long n) => n switch
    {
        < 1_000 => n.ToString(CultureInfo.InvariantCulture),
        < 999_950 => Compact(n / 1e3, "k"),
        < 999_950_000 => Compact(n / 1e6, "M"),
        _ => Compact(n / 1e9, "B"),
    };

    private static string Compact(double value, string unit) =>
        (value >= 100 ? Math.Round(value) : Math.Round(value, 1)).ToString("0.#", CultureInfo.InvariantCulture) + unit;

    /// <summary>Pulls as Docker Hub says them ("1B+"), else counted ("13.6B"); null when it didn't say.</summary>
    public static string? Pulls(long? pulls, string? text) =>
        !string.IsNullOrWhiteSpace(text) ? text.Trim() : pulls is long n and >= 0 ? Count(n) : null;

    /// <summary>"Updated 2 days ago"; null when Docker Hub didn't say (its basic search doesn't).</summary>
    public static string? Updated(DateTime? utc, DateTime now) => utc is { } at ? "Updated " + NewAppText.Ago(at, now) : null;

    /// <summary>When a tag was pushed: "2 days ago"; null when the registry didn't say (tags/list doesn't).</summary>
    public static string? Pushed(DateTime? utc, DateTime now) => utc is { } at ? NewAppText.Ago(at, now) : null;

    /// <summary>A tag's size for this host's platform, as Docker Hub gives it: "26.1 MB"; null when unknown.</summary>
    public static string? Size(long? bytes) => bytes is long n and > 0 ? NewAppText.Size(n) : null;

    /// <summary>Docker Hub's mark on a result, in its own words; null for none.</summary>
    public static string? BadgeWord(HubBadge badge) => badge switch
    {
        HubBadge.Official => "Official",
        HubBadge.VerifiedPublisher => "Verified Publisher",
        HubBadge.SponsoredOss => "Sponsored OSS",
        _ => null,
    };

    /// <summary>The warning on a result or a tag that has no image for this host: "No arm64 image" (its architecture unknown: "No image for this host").</summary>
    public static string NoImageFor(string? architecture) =>
        string.IsNullOrWhiteSpace(architecture) ? "No image for this host" : $"No {architecture.Trim()} image";

    /// <summary>Docker Hub's page for an image, from its parsed name: hub.docker.com/_/nginx, hub.docker.com/r/louislam/uptime-kuma.</summary>
    public static string? HubPage(string name) => ImageReference.Parse(name) is { IsDockerHub: true } r
        ? r.Repository.StartsWith("library/", StringComparison.Ordinal)
            ? "https://hub.docker.com/_/" + r.Repository["library/".Length..]
            : "https://hub.docker.com/r/" + r.Repository
        : null;

    // ---- when Docker Hub or a registry won't answer ----

    /// <summary>When a rate-limited Docker Hub may be asked again: "again in 40 s", "again in 2 min"; null once it may.</summary>
    public static string? Countdown(DateTime? retryAt, DateTime now)
    {
        if (retryAt is not { } at) return null;
        var seconds = (int)Math.Ceiling((at - now).TotalSeconds);
        return seconds <= 0 ? null
            : seconds < 60 ? $"again in {seconds.ToString(CultureInfo.InvariantCulture)} s"
            : $"again in {((seconds + 59) / 60).ToString(CultureInfo.InvariantCulture)} min";
    }

    /// <summary>
    /// The line under the search box when Docker Hub won't answer: its sentence (the search's own, else one for the
    /// problem), then when it may be asked again. Null when there's no problem.
    /// </summary>
    public static string? SearchProblem(LookupProblem problem, string? message, DateTime? retryAt, DateTime now)
    {
        if (problem == LookupProblem.None) return null;
        var line = !string.IsNullOrWhiteSpace(message) ? message.Trim() : problem switch
        {
            LookupProblem.Unreachable => "Docker Hub can't be reached from this host, so there's no search. Type the image's full name, like nginx:1.27 or ghcr.io/owner/app:1.4.",
            LookupProblem.RateLimited => "Docker Hub asked Gitberth to wait before searching again. Type the image's full name meanwhile.",
            _ => "Docker Hub's search didn't answer properly. Type the image's full name meanwhile.",
        };
        return Countdown(retryAt, now) is { } again ? $"{line} · {again}" : line;
    }

    /// <summary>
    /// The line where the tags would be when they can't be listed: the list's sentence, when it may be asked again, and
    /// that the tag can be typed instead. Null when there's no problem.
    /// </summary>
    public static string? TagsProblem(LookupProblem problem, string? message, DateTime? retryAt, DateTime now)
    {
        if (problem == LookupProblem.None) return null;
        var line = !string.IsNullOrWhiteSpace(message) ? message.Trim() : problem switch
        {
            LookupProblem.NotFound => "The registry has no such image.",
            LookupProblem.NeedsSignIn => "The registry wants a sign-in to list its tags: add one in Settings › Registries.",
            LookupProblem.Unreachable => "The registry can't be reached from this host, so its tags can't be listed.",
            LookupProblem.RateLimited => "The registry asked Gitberth to wait before listing tags again.",
            _ => "The registry didn't list the image's tags.",
        };
        line += " Type the tag instead.";
        return Countdown(retryAt, now) is { } again ? $"{line} · {again}" : line;
    }

    /// <summary>What the list shows while it's read: "Listing nginx's tags…".</summary>
    public static string Listing(string image) => $"Listing {image}'s tags…";

    /// <summary>
    /// The line under the list: how many tags were read (of how many, while older pages are left: Load older tags), and
    /// how many of them it leaves out (signatures, CI builds, other systems'), which can still be typed.
    /// </summary>
    public static string TagCount(TagList list)
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        if (list.More) return $"{N(list.Read)} of {N(list.Total)} tags read, newest first.";
        var line = list.Dates ? $"{N(list.Total)} tags." : $"{N(list.Total)} tags, from {list.Registry}, which gives no dates.";
        var left = list.Total - list.Read;
        return left <= 0 ? line : $"{line} {N(left)} {(left == 1 ? "isn't" : "aren't")} listed (signatures, CI builds and the like): type one to use it.";
    }

    // ---- the keyboard and the filter ----

    /// <summary>The highlighted row after ↓ (+1) or ↑ (-1): it wraps round, and is -1 when there's no row.</summary>
    public static int Move(int current, int count, int delta)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return delta >= 0 ? 0 : count - 1;
        return ((current + delta) % count + count) % count;
    }

    /// <summary>
    /// What the tag field's text filters the list by: the text once it was edited, never the selection it shows (a chip
    /// clicked, the tag preselected); null for none.
    /// </summary>
    public static string? Filter(string? text, bool edited) => edited && text?.Trim() is { Length: > 0 } t ? t : null;

    // ---- the choice ----

    /// <summary>The reference New app goes on with: name:tag, name@sha256:… for a digest, name:latest without a tag.</summary>
    public static string Reference(string name, string? tag)
    {
        var t = tag?.Trim() ?? "";
        return t.StartsWith("sha256:", StringComparison.Ordinal) ? $"{name}@{t}" : $"{name}:{(t.Length == 0 ? "latest" : t)}";
    }

    /// <summary>The tag a reference names: its digest, else its tag, else latest.</summary>
    public static string TagOf(ImageReference reference) => reference.Digest ?? reference.Tag ?? "latest";

    /// <summary>The tag the picker selects once the list is read: the one it opened with, else the list's default (D1), else latest.</summary>
    public static string Initial(string? opened, TagList? list) =>
        !string.IsNullOrWhiteSpace(opened) ? opened.Trim() : list?.Default is { Length: > 0 } d ? d : "latest";

    /// <summary>Whether the row is what the app runs now: its digest is among the running image's (RepoDigests).</summary>
    public static bool RunningNow(TagRow row, IReadOnlyCollection<string> digests) => row.Digest is { } d && digests.Contains(d);

    /// <summary>The row of a list that names the tag (its tag, or one of the others), Versions first; null when none does.</summary>
    public static TagRow? RowOf(TagList? list, string tag)
    {
        if (list is null || tag.Length == 0) return null;
        return list.Versions.Concat(list.Other).FirstOrDefault(r => r.Tag == tag || r.Also.Contains(tag));
    }

    /// <summary>
    /// The line under the tag field: what the chosen tag follows, so what arrives as an update. A floating version (1.31)
    /// follows its line's patches, a one-part one (18) a major's releases, a channel (latest, stable) every release; an
    /// exact version is pinned to it, a digest to one image. <paramref name="row"/> is the listed row that names it (null:
    /// it isn't listed), <paramref name="shownAs"/> that row's tag as the chosen variant shows it (1.31.2-alpine).
    /// </summary>
    public static string Follows(string? tag, TagRow? row, string? shownAs = null)
    {
        var t = tag?.Trim() is { Length: > 0 } x ? x : "latest";
        if (t.StartsWith("sha256:", StringComparison.Ordinal)) return "Pinned to one exact image: it never updates.";
        if (row is null) return $"{t} isn't among the tags listed: Check asks the registry for it.";
        const string Every = "every release arrives as an update, majors included.";
        var parts = Parts(t);
        // A channel on a row that names no version (postgres' latest = trixie): it moves, to what can't be said.
        if (parts == 0 && row.Version is null) return $"Follows {t}: {Every}";
        var now = shownAs ?? row.Tag;
        if (t == row.Tag || t == now) return row.PreRelease
            ? $"Pinned to {t}, a pre-release: only rebuilds of it arrive as updates."
            : $"Pinned to {t}: only rebuilds of it arrive as updates.";
        var naming = row.Inferred ? $"likely {now} now" : $"now {now}";
        if (parts == 0) return $"Follows {t}, {naming}: {Every}";
        // As many numbers as the version (v3.3.1 for 3.3.1, 1.31.2-alpine3.24): the same version, so pinned.
        if (parts >= Parts(row.Version ?? row.Tag)) return $"Pinned to {t}: only rebuilds of it arrive as updates.";
        return parts == 1
            ? $"Follows {t}, {naming}: minor releases and patches arrive as updates, never a new major."
            : $"Follows {t}, {naming}: patch releases arrive as updates.";
    }

    // How many numbers a tag's version has: 3 for 1.31.2 and v1.31.2-alpine, 1 for 18, 0 for latest or bookworm.
    private static int Parts(string tag)
    {
        var head = tag.Split('-')[0];
        if (head.Length > 1 && head[0] is 'v' or 'V' && char.IsAsciiDigit(head[1])) head = head[1..];
        var numbers = head.Split('.');
        return numbers.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)) ? numbers.Length : 0;
    }
}
