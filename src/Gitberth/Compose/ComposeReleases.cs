using System.Security.Cryptography;
using System.Text;
using Gitberth.Data;

namespace Gitberth.Compose;

/// <summary>
/// Where a Compose app's releases are kept: /data/compose/&lt;slug&gt;/&lt;release&gt;/ holds a release's files (the repo at
/// its commit, or the Compose file Gitberth keeps), with the file each deploy of it ran (gitberth-&lt;deploy&gt;.json), so a
/// rollback runs a release again with what its bind mounts read; /data/compose/&lt;slug&gt;/data/ holds what the services
/// write into their relative bind mounts, kept from one release to the next. The last few releases are kept, and the one
/// that runs.
/// </summary>
public sealed class ComposeReleases(string root)
{
    /// <summary>How many of the last successful deploys keep their release (and their built images), besides the one that runs.</summary>
    public const int Kept = 3;

    /// <summary>The app's own folder under Gitberth's data: /data/compose/&lt;slug&gt;.</summary>
    public string Root { get; } = root;

    public static ComposeReleases For(GitberthOptions options, string slug) => new(Path.Combine(options.DataDir, "compose", slug));

    /// <summary>A release's folder, by its id (a commit's first 7 characters, or the kept Compose file's).</summary>
    public string Dir(string release) => Path.Combine(Root, release);

    /// <summary>Where what the services write into their relative bind mounts is kept.</summary>
    public string DataDir => Path.Combine(Root, "data");

    /// <summary>
    /// Makes the app's folder, which only Gitberth (root) may open: its releases hold the app's variables (the .env, the
    /// files each deploy ran, an inline app's Compose file). Its containers' bind mounts aren't affected: Docker mounts their
    /// folders itself.
    /// </summary>
    public void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        OwnerOnly(Root, folder: true);
    }

    /// <summary>A file (or folder) only its owner, Gitberth, reads and writes; nothing on Windows, where tests run.</summary>
    public static void OwnerOnly(string path, bool folder = false)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (folder ? UnixFileMode.UserExecute : UnixFileMode.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The file a deploy ran, in its release's folder.</summary>
    public static string ResolvedName(int deploymentId) => $"gitberth-{deploymentId}.json";

    /// <summary>The file a deploy ran, wherever its release is; null when it isn't kept.</summary>
    public string? ResolvedOf(int deploymentId)
    {
        if (!Directory.Exists(Root)) return null;
        var name = ResolvedName(deploymentId);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>A Compose file Gitberth keeps (an inline app's) as a release's id: the start of its SHA-256.</summary>
    public static string IdOf(string yaml) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)))[..7];

    /// <summary>
    /// The deploy a failed one goes back to: the newest one that worked before it, whose file is still kept. Null when none
    /// did (a first deploy), or none is kept.
    /// </summary>
    public static int? PickRollback(IEnumerable<(int Id, DeployStatus Status)> deploys, int current, Func<int, bool> kept) =>
        deploys.Where(d => d.Id != current && d.Id < current && d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id)
            .Select(d => (int?)d.Id).FirstOrDefault(id => kept(id!.Value));

    /// <summary>The deploys whose releases stay: the last <see cref="Kept"/> that worked, and the one that runs now.</summary>
    public static HashSet<int> ToKeep(IEnumerable<(int Id, DeployStatus Status)> deploys, int? running)
    {
        var keep = deploys.Where(d => d.Status == DeployStatus.Succeeded).OrderByDescending(d => d.Id).Take(Kept).Select(d => d.Id).ToHashSet();
        if (running is int id) keep.Add(id);
        return keep;
    }

    /// <summary>
    /// Removes the files of deploys that aren't kept, then each release folder none of the kept ones ran (the kept data
    /// folder stays). Returns the releases removed. What can't be removed now goes the next time.
    /// </summary>
    public List<string> Prune(IReadOnlySet<int> keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(Root)) return removed;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (Path.GetFileName(dir) == "data") continue;
            var any = false;
            foreach (var file in Directory.EnumerateFiles(dir, "gitberth-*.json"))
            {
                if (DeploymentOf(file) is int id && keep.Contains(id)) any = true;
                else Deploy.DeployPaths.TryDeleteFile(file);
            }
            if (any) continue;
            Deploy.DeployPaths.TryDeleteDirectory(dir);
            removed.Add(Path.GetFileName(dir));
        }
        return removed;
    }

    /// <summary>
    /// The deploy a gitberth-&lt;id&gt;.json is of, or a gitberth-&lt;id&gt;.build.json (its file for docker compose build,
    /// while it builds); null for another file.
    /// </summary>
    internal static int? DeploymentOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".build", StringComparison.Ordinal)) name = name[..^".build".Length];
        return name.StartsWith("gitberth-", StringComparison.Ordinal) && int.TryParse(name["gitberth-".Length..], out var id) ? id : null;
    }
}

/// <summary>The .env Gitberth writes next to a Compose file: the app's variables, which Compose fills the file's ${…} with.</summary>
public static class ComposeEnvFile
{
    /// <summary>
    /// The file: the repo's own .env first (<paramref name="repo"/>), then the app's values, which win (Compose takes the
    /// last of a name). A value goes in single quotes, as it is; one with a quote or a line break goes in double quotes,
    /// with \, ", $ and line breaks escaped, so Compose reads back exactly the value.
    /// </summary>
    public static string Write(IEnumerable<(string Key, string Value)> values, string? repo = null)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(repo)) text.Append(repo.TrimEnd()).Append('\n');
        var list = values.Where(v => Detection.EnvRules.IsValidKey(v.Key)).ToList();
        if (list.Count > 0) text.Append("# Set by Gitberth: the app's variables. Changed in Gitberth, not here.\n");
        foreach (var (key, value) in list) text.Append(key).Append('=').Append(Quote(value)).Append('\n');
        return text.ToString();
    }

    internal static string Quote(string value)
    {
        // Compose takes a quote right after a backslash for an escaped one, so a value that ends with a backslash can't be
        // quoted: it goes as it is when Compose reads that back unchanged.
        if (value.EndsWith('\\') && value.Trim() == value && !value.Contains('$') && !value.Contains(" #", StringComparison.Ordinal)
            && !value.Contains('\n') && !value.Contains('\r') && value[0] is not ('\'' or '"' or '`'))
            return value;
        if (!value.Contains('\'') && !value.Contains('\n') && !value.Contains('\r') && !value.EndsWith('\\')) return "'" + value + "'";
        var escaped = new StringBuilder("\"");
        foreach (var c in value)
            escaped.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '$' => "\\$",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => c.ToString(),
            });
        return escaped.Append('"').ToString();
    }
}
