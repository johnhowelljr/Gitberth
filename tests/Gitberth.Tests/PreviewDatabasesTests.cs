using Gitberth.Data;
using Gitberth.Previews;

namespace Gitberth.Tests;

/// <summary>A preview never gets production's database unless it has a value for previews: which variables count, and from where.</summary>
public sealed class PreviewDatabasesTests
{
    [Theory]
    [InlineData("DATABASE_URL", "x", true)]
    [InlineData("database_url", "x", true)]
    [InlineData("SHADOW_DATABASE_URL", "x", true)]
    [InlineData("POSTGRES_PRISMA_URL", "x", true)]
    [InlineData("PGHOST", "db", true)]
    [InlineData("MONGODB_URI", "x", true)]
    [InlineData("REDIS_URL", "x", true)]
    [InlineData("ConnectionStrings__Default", "Host=db", true)]
    [InlineData("ANYTHING", "postgresql://u:p@db/app", true)]
    [InlineData("CACHE", "rediss://default:pw@cache:6380", true)]
    [InlineData("STORE", "mongodb+srv://u:p@cluster0.example.net/app", true)]
    [InlineData("SENTRY_DSN", "https://abc@o1.ingest.sentry.io/2", false)]
    [InlineData("API_URL", "https://api.example.com", false)]
    [InlineData("NODE_ENV", "production", false)]
    public void A_database_is_known_by_its_name_or_its_value(string name, string value, bool database) =>
        Assert.Equal(database, PreviewDatabases.LooksLikeDatabase(name, value));

    [Fact]
    public void What_reaches_a_preview_with_productions_value()
    {
        (string, string, EnvScope)[] own =
        [
            ("DATABASE_URL", "postgres://prod", EnvScope.All),
            ("REDIS_URL", "redis://prod", EnvScope.All),
            ("REDIS_URL", "redis://preview", EnvScope.Preview),
            ("PGHOST", "prod-db", EnvScope.Production),
            ("EMPTY_DATABASE_URL", "", EnvScope.All),
        ];
        (string, string)[] github = [("MONGODB_URI", "mongodb://prod"), ("PGHOST", "prod-db"), ("REDIS_URL", "redis://gh"), ("API_URL", "https://x")];
        var shared = PreviewDatabases.Find(own, github);
        // DATABASE_URL for all deploys; MONGODB_URI and PGHOST from GitHub (a Production value doesn't reach previews, so
        // GitHub's does); REDIS_URL has a value for previews; an empty value is no value.
        Assert.Equal(new[] { ("DATABASE_URL", false), ("MONGODB_URI", true), ("PGHOST", true) }, shared.Select(s => (s.Name, s.FromGitHub)));
    }

    [Fact]
    public void A_value_for_previews_even_productions_own_is_the_owners_choice()
    {
        (string, string, EnvScope)[] own = [("DATABASE_URL", "postgres://prod", EnvScope.All), ("DATABASE_URL", "postgres://prod", EnvScope.Preview)];
        Assert.Empty(PreviewDatabases.Find(own, [("DATABASE_URL", "postgres://prod")]));
    }

    [Fact]
    public void The_refusal_names_them_and_says_where_they_come_from()
    {
        Assert.StartsWith("DATABASE_URL would reach this preview with production's value (from the repo's GitHub variables),",
            PreviewDatabases.Refusal([new("DATABASE_URL", true)], "kanban"));
        var two = PreviewDatabases.Refusal([new("DATABASE_URL", false), new("REDIS_URL", true)], "kanban");
        Assert.StartsWith("DATABASE_URL and REDIS_URL would reach this preview with production's value (from Gitberth and the repo's GitHub variables),", two);
        Assert.Contains("On kanban's Environment tab, give each a value for Previews", two);
    }
}
