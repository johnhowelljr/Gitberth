namespace Gitberth;

/// <summary>
/// Identifies this build of Gitberth. The compiler derives the module ID from what it compiles: it changes when Gitberth's
/// code changes, and also when the compiler or a referenced package does. A redeploy of exactly the same build keeps it.
/// </summary>
public static class BuildStamp
{
    public static readonly string Current = typeof(BuildStamp).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
}
