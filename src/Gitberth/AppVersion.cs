using System.Reflection;

namespace Gitberth;

/// <summary>
/// The version of Gitberth that's running, for the sidebar and Settings › System. The number is Version in Gitberth.csproj,
/// or the release tag the image was built from (v1.2.3 gives 1.2.3). The commit comes from the image build too; a build
/// from a folder has none.
/// </summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>
    /// 1.2.3, or 1.2.3-rc.1 for a pre-release. Only in the end-to-end rig (with TestTimings on), GITBERTH_TEST_VERSION
    /// names another: the same build as a "newer" version, which Gitberth updates itself to there.
    /// </summary>
    public static string Number { get; } = TestVersion() ?? Parse(Informational).Number;

    /// <summary>The rig's stand-in version (see <see cref="Number"/>); an install never sets it, and without the rig's timings it's ignored.</summary>
    public const string TestVariable = "GITBERTH_TEST_VERSION";

    private static string? TestVersion() =>
        Environment.GetEnvironmentVariable(TestTimings.Variable) is { Length: > 0 } && Environment.GetEnvironmentVariable(TestVariable)?.Trim() is { Length: > 0 } version
            ? version
            : null;

    /// <summary>The commit it was built from, 7 characters; empty when unknown.</summary>
    public static string Commit { get; } = Parse(Informational).Commit;

    /// <summary>"1.2.3 (abc1234)", or "1.2.3" without a commit.</summary>
    public static string Display => Commit.Length > 0 ? $"{Number} ({Commit})" : Number;

    /// <summary>Splits an informational version like 1.2.3+abc1234def into the number and a short commit.</summary>
    internal static (string Number, string Commit) Parse(string informational)
    {
        var plus = informational.IndexOf('+');
        var number = plus < 0 ? informational : informational[..plus];
        var commit = plus < 0 ? "" : informational[(plus + 1)..];
        if (commit.Length > 7) commit = commit[..7];
        return (number.Length > 0 ? number : "0.0.0", commit);
    }
}
