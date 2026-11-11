using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

public class BuildSecretsTests
{
    private const string Template = """
        FROM node:24-slim AS build
        WORKDIR /app
        RUN echo before
        COPY . .
        # gitberth:build-args
        RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
        RUN npm run build
        FROM nginx:stable-alpine
        RUN echo final
        """;

    [Fact]
    public void Secrets_are_mounted_into_the_build_steps_instead_of_becoming_args()
    {
        var expanded = DockerfileMarkers.Expand(Template, ["NEXT_PUBLIC_SITE"], [], ["DATABASE_URL", "AUTH_SECRET"]);
        Assert.Equal("""
            FROM node:24-slim AS build
            WORKDIR /app
            RUN echo before
            COPY . .
            ARG NEXT_PUBLIC_SITE
            ARG GITBERTH_SECRETS
            RUN --mount=type=secret,id=DATABASE_URL,env=DATABASE_URL --mount=type=secret,id=AUTH_SECRET,env=AUTH_SECRET --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN --mount=type=secret,id=DATABASE_URL,env=DATABASE_URL --mount=type=secret,id=AUTH_SECRET,env=AUTH_SECRET npm run build
            FROM nginx:stable-alpine
            RUN echo final

            """.Replace("\r\n", "\n"), expanded);
    }

    [Fact]
    public void Whether_secrets_go_in_is_decided_by_the_dockerfile_as_written()
    {
        // Expanding replaces the marker, so checking the expanded text would send secrets in as unused build args.
        Assert.True(DockerfileMarkers.TakesSecrets(Template));
        Assert.False(DockerfileMarkers.TakesSecrets(DockerfileMarkers.Expand(Template, [], [], ["DATABASE_URL"])));
        Assert.False(DockerfileMarkers.TakesSecrets("FROM node:24\nARG DATABASE_URL\nRUN npm ci\n"));
    }

    [Fact]
    public void Without_secrets_the_build_steps_stay_as_written()
    {
        var expanded = DockerfileMarkers.Expand(Template, ["A"], []);
        Assert.DoesNotContain("--mount=type=secret", expanded);
        Assert.DoesNotContain(DockerfileMarkers.SecretsArg, expanded);
    }

    [Fact]
    public void The_steps_that_see_secrets_are_keyed_to_their_values_in_generated_and_marked_custom_dockerfiles_only()
    {
        // A generated Dockerfile: the arg is declared where the secrets are mounted, before the steps that see them.
        var generated = DockerfileMarkers.Expand(DockerfileTemplates.DotNetWeb("10.0", 10, "src/Web/Web.csproj", "Web", needsNode: false), ["PUBLIC_URL"], [], ["API_TOKEN"]);
        Assert.Contains("COPY . .\nARG PUBLIC_URL\nARG GITBERTH_SECRETS\nRUN --mount=type=secret,id=API_TOKEN,env=API_TOKEN --mount=type=cache,id=nuget", generated);
        // A custom one with Gitberth's line, in each stage that has it.
        const string custom = "FROM node:24 AS deps\n# gitberth:build-args\nRUN npm ci\nFROM node:24 AS build\n# gitberth:build-args\nRUN npm run build\nFROM nginx\nRUN echo done\n";
        var expanded = DockerfileMarkers.Expand(custom, [], [], ["NPM_TOKEN"]);
        Assert.Equal(2, expanded.Split('\n').Count(l => l == "ARG GITBERTH_SECRETS"));
        Assert.EndsWith("FROM nginx\nRUN echo done\n", expanded);
        // One without the line is left as it is.
        const string plain = "FROM node:24\nARG NPM_TOKEN\nRUN npm ci\n";
        Assert.Equal(plain, DockerfileMarkers.Expand(plain, [], [], ["NPM_TOKEN"]));
    }

    [Fact]
    public void The_cache_key_changes_with_every_secret_value_and_gives_none_of_them_away()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        EnvValue[] production = [new("DATABASE_URL", "postgres://app:live@db/app", true), new("STRIPE_KEY", "sk_live_1", true)];
        var stamp = BuildSecrets.Stamp(key, production);

        Assert.Matches("^[0-9a-f]{64}$", stamp);
        // The same values in another order: the same key, so the cache is used.
        Assert.Equal(stamp, BuildSecrets.Stamp(key, [production[1], production[0]]));
        // Another scope's value (a preview's test key), a changed one, another name, or another installation: another key.
        Assert.NotEqual(stamp, BuildSecrets.Stamp(key, [production[0], new("STRIPE_KEY", "sk_test_1", true)]));
        Assert.NotEqual(stamp, BuildSecrets.Stamp(key, [production[0]]));
        Assert.NotEqual(stamp, BuildSecrets.Stamp(key, [production[0], new("STRIPE_SECRET", "sk_live_1", true)]));
        Assert.NotEqual(stamp, BuildSecrets.Stamp(key.Select(b => (byte)(b ^ 0xFF)).ToArray(), production));
        // Without the installation's key a guess can't be checked against it: it isn't a plain hash of the values.
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { "DATABASE_URL=postgres://app:live@db/app", "STRIPE_KEY=sk_live_1" }))));
        Assert.NotEqual(plain, stamp);
    }

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    // A build's values: a plain one, and two secrets.
    private static readonly EnvValue[] Values =
    [
        new("PUBLIC_URL", "https://orb.example.com", false),
        new("DATABASE_URL", "postgres://app:live-pw@db/app", true),
        new("API_TOKEN", "tok_live_1", true),
    ];

    // What the build is handed, and how many times the installation's key was asked for.
    private static async Task<(DeployPipeline.BuildValues Values, int KeyAsked)> BuildValuesAsync(string source, bool filledIn, IReadOnlyList<EnvValue> env)
    {
        var asked = 0;
        var values = await DeployPipeline.BuildValuesAsync(source, filledIn, env, new HashSet<string>(), new HashSet<string>(), () =>
        {
            asked++;
            return Task.FromResult(Key);
        });
        return (values, asked);
    }

    private static List<string> Stamps(DeployPipeline.BuildValues values) =>
        values.Args.Where((arg, i) => i > 0 && values.Args[i - 1] == "--build-arg" && arg.StartsWith(DockerfileMarkers.SecretsArg, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task A_generated_or_marked_custom_dockerfile_with_secrets_gets_one_stamp_of_their_values_and_no_value_in_its_arguments()
    {
        var generated = DockerfileTemplates.DotNetWeb("10.0", 10, "src/Web/Web.csproj", "Web", needsNode: false);
        const string custom = "FROM node:24\n# gitberth:build-args\nRUN npm ci\n";
        foreach (var source in new[] { generated, custom })
        {
            var (values, asked) = await BuildValuesAsync(source, filledIn: true, Values);

            var stamp = Assert.Single(Stamps(values));
            Assert.Matches("^GITBERTH_SECRETS=[0-9a-f]{64}$", stamp);
            Assert.Equal($"GITBERTH_SECRETS={BuildSecrets.Stamp(Key, [Values[1], Values[2]])}", stamp);
            Assert.Equal(1, asked);
            // The secrets are BuildKit secrets, read from the CLI's environment; the plain value is a build arg.
            Assert.Equal(["--secret", "id=DATABASE_URL,env=GITBERTH_SECRET_0", "--secret", "id=API_TOKEN,env=GITBERTH_SECRET_1"],
                values.Args.Where((_, i) => values.Args[i] == "--secret" || (i > 0 && values.Args[i - 1] == "--secret")));
            Assert.Equal(("postgres://app:live-pw@db/app", "tok_live_1", "https://orb.example.com"),
                (values.Environment["GITBERTH_SECRET_0"], values.Environment["GITBERTH_SECRET_1"], values.Environment["PUBLIC_URL"]));
            Assert.Contains("PUBLIC_URL", values.Args);
            Assert.DoesNotContain(values.Args, a => a.Contains("live-pw", StringComparison.Ordinal) || a.Contains("tok_live_1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task No_stamp_without_secrets_without_the_marker_or_for_the_repos_own_dockerfile()
    {
        const string marked = "FROM node:24\n# gitberth:build-args\nRUN npm ci\n";
        // Only plain values.
        var (plain, askedPlain) = await BuildValuesAsync(marked, filledIn: true, [Values[0]]);
        Assert.Empty(Stamps(plain));
        Assert.DoesNotContain("--secret", plain.Args);

        // A custom Dockerfile without Gitberth's line: values are build args for the ARGs it declares, through the environment.
        var (unmarked, askedUnmarked) = await BuildValuesAsync("FROM node:24\nARG DATABASE_URL\nARG API_TOKEN\nRUN npm ci\n", filledIn: true, Values);
        Assert.Empty(Stamps(unmarked));
        Assert.DoesNotContain("--secret", unmarked.Args);
        Assert.DoesNotContain(unmarked.Args, a => a.Contains("live-pw", StringComparison.Ordinal) || a.Contains("tok_live_1", StringComparison.Ordinal));

        // The repo's own Dockerfile is built as it is: its secrets mount as BuildKit secrets, but nothing declares the stamp.
        var (own, askedOwn) = await BuildValuesAsync(marked, filledIn: false, Values);
        Assert.Empty(Stamps(own));
        Assert.Contains("id=DATABASE_URL,env=GITBERTH_SECRET_0", own.Args);

        // The installation's key is never asked for when nothing is stamped.
        Assert.Equal((0, 0, 0), (askedPlain, askedUnmarked, askedOwn));
    }

    [Fact]
    public async Task The_installations_key_is_made_once_and_kept_encrypted()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        await using (var ctx = db.CreateDbContext()) await ctx.Database.EnsureCreatedAsync();
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());

        var first = await BuildSecrets.KeyAsync(db, protector);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, await BuildSecrets.KeyAsync(db, protector));
        var saved = await new SettingsService(db, protector).GetAsync(SettingKeys.BuildSecretsKey);
        Assert.NotNull(saved);
        Assert.NotEqual(Convert.ToBase64String(first), saved);
        Assert.Equal(Convert.ToBase64String(first), protector.TryUnprotect(saved));
    }

    [Theory]
    [InlineData("not encrypted by this installation")]
    [InlineData("encrypted, but not a key")]
    public async Task A_key_that_cant_be_read_any_more_is_replaced(string kept)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Factory(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        await using (var ctx = db.CreateDbContext()) await ctx.Database.EnsureCreatedAsync();
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        // The data protection keys were lost (a new one can't decrypt it), or what it decrypts to isn't a key at all.
        var value = kept.StartsWith("encrypted", StringComparison.Ordinal) ? protector.Protect("this is not base64!") : kept;
        await new SettingsService(db, protector).SetAsync(SettingKeys.BuildSecretsKey, value);

        var key = await BuildSecrets.KeyAsync(db, protector);

        Assert.Equal(32, key.Length);
        Assert.Equal(Convert.ToBase64String(key), protector.TryUnprotect((await new SettingsService(db, protector).GetAsync(SettingKeys.BuildSecretsKey))!));
        Assert.Equal(key, await BuildSecrets.KeyAsync(db, protector));
    }

    private sealed class Factory(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    [Fact]
    public void Finds_the_args_a_dockerfile_sets_before_its_first_from()
    {
        const string dockerfile = """
            ARG NODE_VERSION=22 BASE
            ARG REGISTRY
            FROM ${REGISTRY}node:${NODE_VERSION}
            ARG DATABASE_URL
            RUN npm ci
            """;
        Assert.Equal(new HashSet<string> { "NODE_VERSION", "BASE", "REGISTRY" }, DockerfileMarkers.GlobalArgs(dockerfile));
    }

    [Theory]
    [InlineData("redis://:s3cret@cache:6379", true)]
    [InlineData("postgres://kanban:pw@postgres:5432/kanban", true)]
    [InlineData("http://localhost:3000", false)]
    [InlineData("https://example.com/a:b@c", false)]
    public void Spots_passwords_in_urls_even_without_a_user(string value, bool expected) =>
        Assert.Equal(expected, EnvRules.HasCredentials(value));

    [Theory]
    [InlineData("SMTP_PASS", true)]
    [InlineData("DB_PASS_PRIMARY", true)]
    [InlineData("SSH_KEY_PASSPHRASE", true)]
    [InlineData("BYPASS_CACHE", false)]
    [InlineData("COMPASS_URL", false)]
    [InlineData("PASSPORT_COUNTRY", false)]
    public void Treats_pass_names_as_secret_without_catching_bypass_or_compass(string name, bool expected) =>
        Assert.Equal(expected, EnvRules.LooksSecret(name));

    [Theory]
    [InlineData("API-URL")]
    [InlineData("app.url")]
    public void GitHub_variable_names_are_letters_digits_and_underscores(string name) =>
        Assert.NotNull(RepoVariables.Problem(name));

    [Fact]
    public void A_form_that_did_not_touch_its_ports_matches_what_it_loaded()
    {
        var stored = new List<PortMapping> { new() { Id = 2, HostPort = 3001, ContainerPort = 3000 }, new() { Id = 1, HostPort = 9000, ContainerPort = 9000, Protocol = "udp" } };
        var loaded = AppSettingsModel.PortsKey(stored);
        var form = new List<PortRow> { new() { HostPort = 9000, ContainerPort = 9000, Protocol = "udp" }, new() { HostPort = 3001, ContainerPort = 3000, Protocol = "tcp" } };
        Assert.Equal(loaded, AppSettingsModel.PortsKey(form));
        form[1].ContainerPort = 80;
        Assert.NotEqual(loaded, AppSettingsModel.PortsKey(form));
    }
}
