namespace Gitberth.Detection;

/// <summary>Reading a Python project's files: small text files only, and only the code that belongs to the app.</summary>
internal static class PythonFiles
{
    private const int MaxBytes = 512 * 1024;

    // Folders that hold no app code: virtualenvs, caches, tests, migrations and docs.
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".venv", "venv", "site-packages", "__pycache__", "node_modules", ".tox", ".nox", ".git", "tests", "test", "testing",
        "migrations", "docs", "htmlcov", ".mypy_cache", ".pytest_cache", ".ruff_cache",
    };

    /// <summary>A file's text, or null when it's missing, unreadable or too big to be source.</summary>
    public static string? Read(RepoFiles files, string relativePath) => files.Exists(relativePath) ? ReadPath(files.Full(relativePath)) : null;

    public static string? ReadPath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a file is part of the app's own code, rather than a test, a migration, a virtualenv or tooling.</summary>
    public static bool IsAppCode(RepoFiles files, string relativePath)
    {
        var parts = relativePath.Split('/');
        var name = parts[^1];
        if (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
            || name is "conftest.py" or "setup.py" or "noxfile.py" or "fabfile.py") return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (IgnoredFolders.Contains(parts[i])) return false;
            // A virtualenv under another name ("env", ".env311") has a pyvenv.cfg at its top.
            if (files.Exists(string.Join('/', parts.Take(i + 1)) + "/pyvenv.cfg")) return false;
        }
        return true;
    }

    /// <summary>The app's Python files, shallowest first.</summary>
    public static IEnumerable<string> Sources(RepoFiles files) =>
        files.WithExtension(".py").Where(f => IsAppCode(files, f)).OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal);

    /// <summary>"app/main.py" → "app.main", "pkg/__init__.py" → "pkg"; null when a part isn't a valid module name.</summary>
    public static string? ModuleName(string relativePath)
    {
        if (!relativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = relativePath[..^3].Split('/').ToList();
        if (parts.Count > 1 && parts[^1] == "__init__") parts.RemoveAt(parts.Count - 1);
        return parts.All(IsIdentifier) ? string.Join('.', parts) : null;
    }

    public static bool IsIdentifier(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A path relative to a folder ("requirements" + "../base.txt"), normalized; null when it leaves the app's folder.</summary>
    public static string? Combine(string dir, string path)
    {
        if (path.StartsWith('/') || path.StartsWith('~') || path.Contains('$') || path.Contains("://", StringComparison.Ordinal)) return null;
        var parts = dir.Length == 0 ? new List<string>() : dir.Split('/').ToList();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
