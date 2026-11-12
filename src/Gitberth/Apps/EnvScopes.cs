using Gitberth.Data;

namespace Gitberth.Apps;

/// <summary>
/// Which of a Git app's variables a deploy gets, as Vercel's environments: production (the app's own container) gets the
/// All and Production ones, a preview its parent's All and Preview ones. A Production or Preview value wins over an All
/// one with the same name. The repo's GitHub variables have no scope: every deploy gets them.
/// </summary>
public static class EnvScopes
{
    /// <summary>The variables a deploy gets, in the order they were added.</summary>
    /// <param name="preview">A preview's deploy (its parent's variables are passed); false for production.</param>
    public static List<EnvVar> ForDeploy(IEnumerable<EnvVar> vars, bool preview) => Pick(vars, v => v.Scope, v => v.Key, preview).OrderBy(v => v.Id).ToList();

    /// <summary>The same choice over the settings' rows (the Environment tab's), in their order.</summary>
    public static List<EnvSetting> ForDeploy(IEnumerable<EnvSetting> vars, bool preview) => Pick(vars, v => v.Scope, v => v.Key, preview).ToList();

    private static IEnumerable<T> Pick<T>(IEnumerable<T> vars, Func<T, EnvScope> scopeOf, Func<T, string> keyOf, bool preview)
    {
        var scope = preview ? EnvScope.Preview : EnvScope.Production;
        var list = vars.Where(v => scopeOf(v) == EnvScope.All || scopeOf(v) == scope).ToList();
        var scoped = list.Where(v => scopeOf(v) == scope).Select(keyOf).ToHashSet(StringComparer.Ordinal);
        return list.Where(v => scopeOf(v) == scope || !scoped.Contains(keyOf(v)));
    }

    /// <summary>The scope as the Environment tab names it.</summary>
    public static string Label(EnvScope scope) => scope switch
    {
        EnvScope.Production => "Production",
        EnvScope.Preview => "Preview",
        _ => "All",
    };

    /// <summary>
    /// A variable's name with its scope, where two of the same name have to be told apart (a review line, a problem):
    /// API_URL, API_URL (Production), API_URL (Preview).
    /// </summary>
    public static string Named(string key, EnvScope scope) => scope == EnvScope.All ? key : $"{key} ({Label(scope)})";
}
