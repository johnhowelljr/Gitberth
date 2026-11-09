using System.Security.Cryptography;
using Gitberth.Apps;
using Gitberth.Images;

namespace Gitberth.Sources;

/// <summary>
/// What a few well-known images need before they start, filled in for a new app: postgres, mysql and mariadb refuse to
/// start without a password, so one is generated and marked secret. Only a variable the app doesn't have yet is added,
/// and never one an image's other variables make unnecessary (POSTGRES_HOST_AUTH_METHOD, MYSQL_RANDOM_ROOT_PASSWORD…).
/// </summary>
public static class ImageHints
{
    private sealed record Hint(string[] Images, string Variable, string[] Unless, string Why);

    private static readonly Hint[] Hints =
    [
        new(["postgres", "postgis/postgis", "timescale/timescaledb", "timescale/timescaledb-ha", "pgvector/pgvector"], "POSTGRES_PASSWORD",
            ["POSTGRES_PASSWORD_FILE", "POSTGRES_HOST_AUTH_METHOD"], "postgres won't start without it: it's the password of the postgres user."),
        new(["mysql", "percona", "percona/percona-server"], "MYSQL_ROOT_PASSWORD",
            ["MYSQL_ROOT_PASSWORD_FILE", "MYSQL_ALLOW_EMPTY_PASSWORD", "MYSQL_RANDOM_ROOT_PASSWORD"], "mysql won't start without it: it's the password of its root user."),
        new(["mariadb"], "MARIADB_ROOT_PASSWORD",
            ["MARIADB_ROOT_PASSWORD_FILE", "MARIADB_ROOT_PASSWORD_HASH", "MARIADB_ALLOW_EMPTY_ROOT_PASSWORD", "MARIADB_RANDOM_ROOT_PASSWORD",
             "MYSQL_ROOT_PASSWORD", "MYSQL_ROOT_PASSWORD_FILE", "MYSQL_ALLOW_EMPTY_PASSWORD", "MYSQL_RANDOM_ROOT_PASSWORD"],
            "mariadb won't start without it: it's the password of its root user."),
    ];

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>
    /// <paramref name="env"/> with what the image needs added (a generated password, marked secret), and a note for each.
    /// An image Gitberth has no hint for gets the variables back as they are.
    /// </summary>
    public static (IReadOnlyList<EnvSetting> Env, IReadOnlyList<ParseIssue> Notes) For(string image, IReadOnlyList<EnvSetting> env)
    {
        if (ImageReference.Parse(image) is not { } reference) return (env, []);
        var own = env.Where(e => !e.FromImage).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var result = env.ToList();
        var notes = new List<ParseIssue>();
        foreach (var hint in Hints.Where(h => h.Images.Contains(reference.Name, StringComparer.Ordinal)))
        {
            if (own.Contains(hint.Variable) || hint.Unless.Any(own.Contains)) continue;
            result.RemoveAll(e => e.Key == hint.Variable);
            result.Add(new EnvSetting(hint.Variable, Password(), Secret: true));
            notes.Add(new ParseIssue(ParseSeverity.Note, $"Gitberth generated {hint.Variable} and marked it secret: {hint.Why} Change it here if you have one already.", hint.Variable));
        }
        return (result, notes);
    }

    /// <summary>24 letters and digits, without the ones that read alike (0 O 1 l I): safe in a URL and a shell.</summary>
    internal static string Password() => RandomNumberGenerator.GetString(Alphabet, 24);
}
