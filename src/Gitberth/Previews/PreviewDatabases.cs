using System.Text.RegularExpressions;
using Gitberth.Data;

namespace Gitberth.Previews;

/// <summary>
/// A preview never uses production's database unless it's told to. A variable that names a database (DATABASE_URL, a
/// postgres:// value, …) and would reach a preview with production's value (set in Gitberth for all deploys, or one of the
/// repo's GitHub variables) stops the preview's deploy before anything is built: a preview's build can run migrations, and
/// its code is a pull request's. A value for Previews takes its place, even the same one when previews may share it.
/// </summary>
public static partial class PreviewDatabases
{
    /// <summary>A variable that would reach previews with production's value, and where that value comes from.</summary>
    public sealed record Shared(string Name, bool FromGitHub);

    /// <summary>Whether a variable says where a database is: by its name, or by its value's scheme.</summary>
    public static bool LooksLikeDatabase(string name, string value) => DatabaseName().IsMatch(name) || DatabaseUrl().IsMatch(value.Trim());

    /// <param name="own">The app's own variables (production's app), with their values and where they apply.</param>
    /// <param name="github">The repo's GitHub variables, which every deploy gets unless the app has a value of its own.</param>
    public static List<Shared> Find(IEnumerable<(string Key, string Value, EnvScope Scope)> own, IEnumerable<(string Name, string Value)> github)
    {
        var set = own.Where(v => v.Value.Length > 0).ToList();
        var forPreviews = set.Where(v => v.Scope == EnvScope.Preview).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previewGets = set.Where(v => v.Scope is EnvScope.All or EnvScope.Preview).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = set.Where(v => v.Scope == EnvScope.All && !forPreviews.Contains(v.Key) && LooksLikeDatabase(v.Key, v.Value))
            .Select(v => new Shared(v.Key, FromGitHub: false))
            .Concat(github.Where(v => v.Value.Length > 0 && !previewGets.Contains(v.Name) && LooksLikeDatabase(v.Name, v.Value))
                .Select(v => new Shared(v.Name, FromGitHub: true)));
        return shared.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Why a preview isn't deployed: what reaches it, from where, and what to do.</summary>
    public static string Refusal(IReadOnlyList<Shared> shared, string parent)
    {
        var names = Join(shared.Select(s => s.Name).ToList());
        var from = shared.All(s => s.FromGitHub) ? "from the repo's GitHub variables"
            : shared.Any(s => s.FromGitHub) ? "from Gitberth and the repo's GitHub variables" : "set in Gitberth for all deploys";
        var it = shared.Count == 1 ? "it" : "each";
        return $"{names} would reach this preview with production's value ({from}), so the preview's build and code would use production's database. "
            + $"On {parent}'s Environment tab, give {it} a value for Previews: a database of the previews' own, or the same value if previews may share production's.";
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    // DATABASE_URL and friends, Prisma's and Vercel's Postgres names, MySQL, MongoDB, Redis, PG*, and .NET's ConnectionStrings__*.
    // Not every *_DSN: SENTRY_DSN is no database.
    [GeneratedRegex(@"^(DATABASE_(URL|URI|DSN|HOST|NAME)|DB_(URL|URI|DSN|HOST|NAME|CONNECTION)|.+_DATABASE_URL|POSTGRES(QL)?_(URL|URI|HOST|DATABASE|PRISMA_URL|URL_NON_POOLING)|PG(HOST|DATABASE|URL)|MYSQL_(URL|URI|HOST|DATABASE)|MONGO(DB)?_(URL|URI)|REDIS_(URL|URI|TLS_URL)|KV_URL|CONNECTIONSTRINGS__.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"^(postgres(ql)?|mysql|mariadb|mongodb(\+srv)?|redis|rediss|sqlserver|mssql|cockroachdb)://", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseUrl();
}
