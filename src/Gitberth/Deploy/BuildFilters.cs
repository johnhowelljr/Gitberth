using System.Text;
using System.Text.RegularExpressions;
using Gitberth.Data;

namespace Gitberth.Deploy;

/// <summary>Why a new commit wasn't deployed: its message asked not to be, or nothing the app builds from changed.</summary>
public enum SkipKind { Asked, Filtered }

/// <summary>A new commit that isn't deployed, and the one line that says why (<see cref="Deployment.SkipReason"/>).</summary>
public sealed record SkipVerdict(SkipKind Kind, string Reason);

/// <summary>
/// Build filters, like Vercel's Ignored Build Step and Railway's watch paths: a new commit deploys by itself only when it
/// changes a file the app's Watch paths match (any file when there are none), leaving out the files its Ignored paths
/// match. A commit whose message carries a marker such as [skip ci] isn't deployed by itself either. Only automatic
/// deploys of a new commit are ever skipped (<see cref="CanSkip"/>): anything a person or Gitberth itself starts builds.
/// </summary>
public static class BuildFilters
{
    /// <summary>The most patterns one field takes.</summary>
    public const int MaxPatterns = 50;

    /// <summary>The longest pattern.</summary>
    public const int MaxLength = 200;

    /// <summary>The longest list of files a skip's reason names; the rest are counted.</summary>
    public const int NamesShown = 5;

    /// <summary>What a commit message says to be left alone, in any case, anywhere in its subject or body: the CI markers people already write, and Gitberth's own.</summary>
    public static readonly IReadOnlyList<string> Markers = ["[skip deploy]", "[deploy skip]", "[skip gitberth]", "[skip ci]", "[ci skip]", "[no ci]"];

    /// <summary>
    /// Whether a deploy may be skipped: only one the watcher or GitHub's webhook started for a new commit. A manual deploy,
    /// a first one, a start, a retry, a promote, a rollback, a restart, a settings change and a deploy hook always build.
    /// </summary>
    public static bool CanSkip(DeployTrigger trigger) => trigger is DeployTrigger.Poll or DeployTrigger.Webhook;

    /// <summary>The marker the message carries, as it's written there; null when it has none.</summary>
    public static string? MarkerIn(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var best = -1;
        string? found = null;
        // The first one in the message, so the reason quotes what the author wrote first.
        foreach (var marker in Markers)
        {
            var at = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                found = message.Substring(at, marker.Length);
            }
        }
        return found;
    }

    /// <summary>The verdict for a commit whose message asks not to be deployed; null when it doesn't.</summary>
    public static SkipVerdict? ForMessage(string? message) =>
        MarkerIn(message) is { } marker ? new SkipVerdict(SkipKind.Asked, $"The commit message says {marker}") : null;

    /// <summary>A field as it's kept: lines without trailing blanks, no blank lines at either end, Unix line ends; null when it's empty.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    /// <summary>The patterns a field holds, in order: blank lines and # comments left out.</summary>
    public static IReadOnlyList<string> Patterns(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : Lines(text).Where(l => l.Text.Length > 0 && l.Text[0] != '#').Select(l => l.Text).ToList();

    /// <summary>Whether the app has any build filter: without one, a commit's files are never compared.</summary>
    public static bool Any(string? watch, string? ignored) => Patterns(watch).Count > 0 || Patterns(ignored).Count > 0;

    /// <summary>What's wrong with a field, said under its label ("Watch paths: …"); null when every pattern can be used.</summary>
    public static string? Problem(string? text, string label)
    {
        var lines = Lines(text).Where(l => l.Text.Length > 0 && l.Text[0] != '#').ToList();
        if (lines.Count > MaxPatterns) return $"{label}: up to {MaxPatterns} patterns, and there are {lines.Count}.";
        foreach (var (number, line) in lines)
        {
            if (line.Length > MaxLength) return $"{label}: line {number} is longer than {MaxLength} characters.";
            if (line[0] == '!')
                return $"{label}: line {number} starts with !, and patterns that bring files back aren't supported. Leave those files out of the other patterns instead.";
            if (PathPattern.Problem(line) is { } problem) return $"{label}: line {number} ({line}) {problem}";
        }
        return null;
    }

    /// <summary>
    /// The verdict on a commit's changed files (paths from the repo's root): skipped when none of them is one the app
    /// builds from: each is left out by <paramref name="ignored"/>, or, when there are <paramref name="watch"/> patterns,
    /// matches none of them. Null: it deploys. A commit that changed nothing is skipped too.
    /// </summary>
    public static SkipVerdict? ForChanges(IReadOnlyList<string> changed, string? watch, string? ignored) =>
        Relevant(changed, watch, ignored) is null ? new SkipVerdict(SkipKind.Filtered, FilesReason(changed)) : null;

    /// <summary>The first changed file the app builds from, as the filters see it; null when there's none.</summary>
    public static string? Relevant(IReadOnlyList<string> changed, string? watch, string? ignored)
    {
        var watching = Patterns(watch).Select(PathPattern.Parse).ToList();
        var leftOut = Patterns(ignored).Select(PathPattern.Parse).ToList();
        return changed.FirstOrDefault(path => !leftOut.Any(p => p.Matches(path)) && (watching.Count == 0 || watching.Any(p => p.Matches(path))));
    }

    /// <summary>A filtered commit's reason: the files it changed, the first few by name.</summary>
    public static string FilesReason(IReadOnlyList<string> changed)
    {
        if (changed.Count == 0) return "Nothing changed since the running commit";
        var names = string.Join(", ", changed.Take(NamesShown));
        return $"Only files the build filters leave out changed: {names}" + (changed.Count > NamesShown ? $" and {changed.Count - NamesShown} more" : "");
    }

    // Each line with its number (from 1), trimmed.
    private static IEnumerable<(int Number, string Text)> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((l, i) => (i + 1, l.Trim()));
}

/// <summary>
/// One build filter pattern, gitignore-style, relative to the repo's root: * matches within one path segment, ** any
/// number of folders, ? one character, and \ takes the next character as it is. A trailing / means a folder and
/// everything under it. A pattern without a slash matches a name at any depth (*.md, README.md); one with a slash at its
/// start or in its middle is anchored to the root (docs/**, /website/). A leading ./ anchors too. A pattern that matches a
/// folder matches every file under it. Matching is case-sensitive, as git's paths are.
/// </summary>
public sealed class PathPattern
{
    private readonly Regex _regex;

    private PathPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    /// <summary>The pattern as it was written.</summary>
    public string Text { get; }

    /// <summary>Whether the path (from the repo's root, with / between folders) is the pattern's, or under a folder that is.</summary>
    public bool Matches(string path) => _regex.IsMatch(path);

    /// <summary>Why the pattern can't be used ("doesn't name a file or folder."), or null.</summary>
    public static string? Problem(string pattern)
    {
        var (body, _, _) = Split(pattern);
        if (body.Length == 0) return "doesn't name a file or folder.";
        if (body.Split('/').Any(s => s is ".." or ".")) return "leaves the repo or names . or ..: patterns are relative to the repo's root.";
        if (body.Contains("//", StringComparison.Ordinal)) return "has an empty folder name (//).";
        return null;
    }

    /// <summary>The pattern, ready to match; one <see cref="Problem"/> refuses never matches anything.</summary>
    public static PathPattern Parse(string pattern)
    {
        var trimmed = pattern.Trim();
        if (Problem(trimmed) is not null) return new PathPattern(trimmed, new Regex("(?!)", RegexOptions.CultureInvariant));
        var (body, anchored, folder) = Split(trimmed);
        var segments = body.Split('/');
        var sb = new StringBuilder(anchored ? "^" : "(?:^|/)");
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (segments[i] == "**")
            {
                // A whole segment of **: any number of folders, or (at the end) everything below.
                sb.Append(last ? ".*" : "(?:[^/]*/)*");
                continue;
            }
            AppendSegment(sb, segments[i]);
            if (!last) sb.Append('/');
        }
        // A folder pattern needs something under it; any other matches the path itself or what's under it.
        sb.Append(folder ? "/" : "(?:/|$)");
        // Linear time whatever the pattern: two hundred characters of stars can't make a deploy wait.
        return new PathPattern(trimmed, new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));
    }

    // The pattern without its leading / or ./ and trailing /: whether it's anchored to the root, and whether it names a folder.
    private static (string Body, bool Anchored, bool Folder) Split(string pattern)
    {
        var p = pattern.Trim();
        var anchored = false;
        if (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
            anchored = true;
        }
        // A trailing slash means a folder, and doesn't anchor it: docs/ is any folder called docs, as in .gitignore.
        var folder = p.EndsWith('/');
        p = p.TrimEnd('/');
        if (p.StartsWith('/'))
        {
            p = p.TrimStart('/');
            anchored = true;
        }
        // A slash in the middle anchors it too.
        anchored |= p.Contains('/');
        return (p, anchored, folder);
    }

    private static void AppendSegment(StringBuilder sb, string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            switch (c)
            {
                case '\\' when i + 1 < segment.Length:
                    sb.Append(Regex.Escape(segment[++i].ToString()));
                    break;
                case '*':
                    // A run of stars inside a segment is one star, as git has it.
                    while (i + 1 < segment.Length && segment[i + 1] == '*') i++;
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }
}
