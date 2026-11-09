namespace Gitberth.Detection;

// Saved by name (AppDefinition.DetectedKind), so new kinds go at the end.
public enum ProjectKind { Unknown, Dockerfile, DotNetWeb, DotNetWorker, DotNetWasm, NodeServer, NextJs, Nuxt, SvelteKit, NodeSpa, Static, Python, Go, Ruby, Php, Java, Rust, Deno }

public static class ProjectKinds
{
    public static bool IsNode(this ProjectKind kind) =>
        kind is ProjectKind.NodeServer or ProjectKind.NextJs or ProjectKind.Nuxt or ProjectKind.SvelteKit or ProjectKind.NodeSpa;

    public static bool IsDotNet(this ProjectKind kind) =>
        kind is ProjectKind.DotNetWeb or ProjectKind.DotNetWorker or ProjectKind.DotNetWasm;
}

/// <summary>An environment variable the app probably needs, and where Gitberth saw it.</summary>
public sealed record EnvSuggestion(string Key, string Value, bool IsSecret, bool BuildTime, string Source);

/// <param name="ProjectHint">Which .NET project, or which Dockerfile variant (like "Dockerfile.prod"), when there are several.</param>
/// <param name="AppDir">The app's folder in the repo, when it isn't at the root.</param>
public sealed record AnalyzeOptions(string? ProjectHint = null, int DefaultNodeMajor = NodeVersions.DefaultMajor, string? AppDir = null);

/// <summary>Something in a repo that Gitberth can build and run, ranked by how likely it is the one wanted.</summary>
/// <param name="Dir">Its folder ("" for the repo root); becomes the app's Subfolder.</param>
/// <param name="Hint">The .NET project or Dockerfile variant to use; becomes the app's ProjectHint.</param>
public sealed record AppCandidate(string Dir, string? Hint, ProjectKind Kind, string Title, string Detail, int Score);

public sealed class DetectionResult
{
    public ProjectKind Kind { get; set; }
    public string Title { get; set; } = "";
    public int? ContainerPort { get; set; }
    /// <summary>Generated Dockerfile; null when the repo's own Dockerfile is used or nothing was detected.</summary>
    public string? Dockerfile { get; set; }
    /// <summary>The repo's own Dockerfile, relative to the repo root, when <see cref="Kind"/> is Dockerfile.</summary>
    public string? RepoDockerfile { get; set; }
    /// <summary>The folder Docker builds from, relative to the repo root: "" for the whole repo, or the app's own folder.</summary>
    public string BuildContext { get; set; } = "";
    public string? DockerIgnore { get; set; }
    public string? SelectedProject { get; set; }
    public List<string> ProjectCandidates { get; set; } = [];
    public List<string> Evidence { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<EnvSuggestion> EnvSuggestions { get; } = [];

    public bool CanBuild => Kind != ProjectKind.Unknown;
}
