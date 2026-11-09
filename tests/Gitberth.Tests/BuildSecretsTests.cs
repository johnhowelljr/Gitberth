using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Services;

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
    public void Without_secrets_the_build_steps_stay_as_written() =>
        Assert.DoesNotContain("--mount=type=secret", DockerfileMarkers.Expand(Template, ["A"], []));

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
