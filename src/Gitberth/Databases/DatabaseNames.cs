using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Databases;

/// <summary>
/// The names Gitberth gives what it makes for an app's databases: a Postgres database and its role (from the app's slug,
/// 63 characters at most, never one the server has already), a preview's (its production database's and its own), and a
/// Redis container's network alias, volume and container. Pure.
/// </summary>
public static class DatabaseNames
{
    /// <summary>Postgres's longest name (NAMEDATALEN - 1).</summary>
    public const int MaxLength = 63;

    /// <summary>The variable an app's Postgres database's connection string goes in unless chosen otherwise.</summary>
    public const string PostgresVariable = "DATABASE_URL";

    /// <summary>The variable an app's Redis's connection string goes in unless chosen otherwise.</summary>
    public const string RedisVariable = "REDIS_URL";

    /// <summary>
    /// The label on a database container that's part of an app (its Redis), next to gitberth.app: it's the app's, hidden
    /// among its app's own containers, and never an app of its own.
    /// </summary>
    public const string MemberLabel = "gitberth.database";

    /// <summary>Whether a container is one of an app's own databases (<see cref="MemberLabel"/>).</summary>
    public static bool IsMember(IReadOnlyDictionary<string, string> labels) => labels.ContainsKey(MemberLabel) && labels.ContainsKey("gitberth.app");

    /// <summary>
    /// The name of an app's database and of its role (the same, so they're easy to tell together): the slug with each '-' an
    /// '_' (shop, my_shop), then _2, _3… while the server has a database or a role by that name, or Gitberth keeps one by
    /// it. Compared without case, so nothing is ever made next to one that only differs in case.
    /// </summary>
    /// <param name="taken">Every database and role name the server has, and the names Gitberth keeps for it.</param>
    public static string ForApp(string slug, IReadOnlySet<string> taken) => Free(Identifier(slug), taken);

    /// <summary>
    /// A preview's database (and role): its production database's name and what the preview is of, as in shop_pr_12 for
    /// the preview shop-pr-12 of shop; shortened with a hash past 63 characters, and unique as <see cref="ForApp"/> is.
    /// </summary>
    /// <param name="production">The production database's name.</param>
    /// <param name="previewSlug">The preview's slug (shop-pr-12).</param>
    /// <param name="parentSlug">Its parent's slug (shop): what the preview's slug starts with is left out.</param>
    public static string ForPreview(string production, string previewSlug, string parentSlug, IReadOnlySet<string> taken)
    {
        var own = previewSlug.StartsWith(parentSlug + "-", StringComparison.Ordinal) && previewSlug.Length > parentSlug.Length + 1
            ? previewSlug[(parentSlug.Length + 1)..]
            : previewSlug;
        return Free(Identifier(production) + "_" + Identifier(own), taken);
    }

    // The name, else with _2, _3… (each kept within 63 characters, the number included), whichever is free first.
    private static string Free(string name, IReadOnlySet<string> taken)
    {
        bool Taken(string candidate) => taken.Contains(candidate) || taken.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase));
        var first = Shorten(name);
        if (!Taken(first)) return first;
        for (var n = 2; ; n++)
        {
            var suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = Shorten(name, MaxLength - suffix.Length) + suffix;
            if (!Taken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// A name in lower-case letters, digits and '_' (every other character an '_', runs of them one), never empty: what a
    /// Postgres name and a URL take as they are.
    /// </summary>
    public static string Identifier(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            var keep = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
            if (keep) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        return name.Length == 0 ? "app" : name;
    }

    /// <summary>The name as it is when it fits, else its beginning and a hash of the whole: shop_a_very_long…_3f9a2c.</summary>
    public static string Shorten(string name, int max = MaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 8);
        if (name.Length <= max) return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..6];
        return name[..(max - 7)].TrimEnd('_') + "_" + hash;
    }

    /// <summary>
    /// The name apps reach an app's Redis by on the shared network: &lt;slug&gt;-redis, then -redis-2… while it's taken
    /// (another app's slug, a name on the network, or one another Redis keeps). Gitberth keeps it from ever becoming an
    /// app's slug (AppService.ChooseSlugAsync).
    /// </summary>
    public static string RedisAlias(string slug, Func<string, bool> taken)
    {
        var name = $"{slug}-redis";
        if (!taken(name)) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// An app's Redis container: its own container's name and _redis (gb-shop_redis). The '_' keeps it apart from every
    /// app's container (gb-&lt;slug&gt; has none) and from a deploy's temporary ones (_next, _prev).
    /// </summary>
    public static string RedisContainer(string appContainer) => appContainer + "_redis";

    /// <summary>The named volume a Redis keeps its data in (its appendonly file): gb-shop_redis-data.</summary>
    public static string RedisVolume(string appContainer) => appContainer + "_redis-data";
}
