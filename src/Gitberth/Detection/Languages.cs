namespace Gitberth.Detection;

/// <summary>What a language's detector found.</summary>
/// <param name="Strong">
/// A web framework, an app server or a Procfile says this is the app. Then it wins over a package.json in the same folder
/// that only builds front-end assets (Laravel's Vite, Rails' esbuild, Django's Tailwind), though not over a Node server.
/// </param>
internal sealed record LanguageMatch(DetectionResult Result, bool Strong);

/// <summary>
/// A language Gitberth builds without a Dockerfile, besides .NET and Node.js. Each one lives in its own file and owns its
/// detection, its generated Dockerfile, the environment variables its code reads, and the files that mark its projects.
/// </summary>
internal interface ILanguage
{
    ProjectKind Kind { get; }

    /// <summary>The <see cref="AnalyzeOptions.ProjectHint"/> that picks this language in a folder that holds another kind of project too.</summary>
    string Hint { get; }

    /// <summary>File names that make a folder worth analyzing in a repo with several apps, like pyproject.toml or go.mod.</summary>
    IReadOnlyList<string> Markers { get; }

    /// <summary>
    /// The app in <paramref name="appDir"/> (relative to <paramref name="repoRoot"/>; <paramref name="files"/> lists that
    /// folder), or null when the folder isn't a runnable project in this language. The result builds from the app's folder.
    /// </summary>
    LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options);

    /// <summary>Suggests the environment variables the app's code reads: add(key, example value, where it was seen).</summary>
    void ScanEnv(RepoFiles files, Action<string, string, string> add);
}

internal static class Languages
{
    /// <summary>Every language, in the order they're tried when a folder could be more than one.</summary>
    public static IReadOnlyList<ILanguage> All { get; } =
        [new PythonLanguage(), new RubyLanguage(), new PhpLanguage(), new JavaLanguage(), new GoLanguage(), new RustLanguage(), new DenoLanguage()];

    public static ILanguage? For(ProjectKind kind) => All.FirstOrDefault(l => l.Kind == kind);

    public static ILanguage? ForHint(string? hint) => hint is null ? null : All.FirstOrDefault(l => l.Hint == hint);

    /// <summary>Every marker file name, for the app finder.</summary>
    public static IReadOnlySet<string> MarkerNames { get; } = All.SelectMany(l => l.Markers).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
