namespace Gitberth.Detection;

/// <summary>A bounded index of a checked-out repo's files ('/'-separated relative paths), skipping dependency and build output folders.</summary>
public sealed class RepoFiles
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".next", ".nuxt", ".output", ".svelte-kit", "dist", "build", "out",
        "coverage", ".vs", ".idea", ".vscode", "vendor", ".yarn", ".pnpm-store", "TestResults", "wwwroot",
    };

    private readonly HashSet<string> _set;

    private RepoFiles(string root, List<string> files)
    {
        Root = root;
        Files = files;
        _set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; }
    public IReadOnlyList<string> Files { get; }

    public static RepoFiles Scan(string root, int maxFiles = 20_000)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && files.Count < maxFiles)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (files.Count >= maxFiles) break;
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(sub);
                    if (!SkipDirs.Contains(info.Name) && (info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        files.Sort(StringComparer.Ordinal);
        return new RepoFiles(root, files);
    }

    public bool Exists(string relativePath) => _set.Contains(relativePath.TrimStart('/'));

    public string Full(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public IEnumerable<string> WithExtension(params string[] extensions) =>
        Files.Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    public static string Join(string dir, string name) => dir.Length == 0 ? name : $"{dir}/{name}";
}
