using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Network;
using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>
/// A Git app's RAILPACK_ variable can't be secret: Railpack writes it into its plan and the image. The rule that says
/// which would be (marked so, or holding a secret once its $NAME references are left out), and that ordinary settings
/// never trip it: commands with $PORT, tool and package lists, versions, cache names.
/// </summary>
public class RailpackSettingsTests
{
    public static readonly TheoryData<string, string> Ordinary = new()
    {
        { "RAILPACK_START_CMD", "node server.js --port $PORT --host 0.0.0.0" },
        { "RAILPACK_START_CMD", "./bin/server -p ${PORT}" },
        { "RAILPACK_BUILD_CMD", "npm run build && npm prune --omit=dev" },
        { "RAILPACK_INSTALL_CMD", "npm ci --no-audit" },
        { "RAILPACK_PACKAGES", "node@22 python@3.13" },
        { "RAILPACK_BUILD_APT_PACKAGES", "build-essential libpq-dev" },
        { "RAILPACK_DEPLOY_APT_PACKAGES", "ffmpeg imagemagick" },
        { "RAILPACK_NODE_VERSION", "22.11.0" },
        { "RAILPACK_PYTHON_VERSION", "3.13" },
        { "RAILPACK_DISABLE_CACHES", "*" },
        { "RAILPACK_DISABLE_CACHES", "npm-install pip" },
        { "RAILPACK_SPA_OUTPUT_DIR", "dist" },
        { "RAILPACK_CONFIG_FILE", "deploy/railpack.json" },
        // A secret named, not written: the way the refusal says to do it.
        { "RAILPACK_START_CMD", "./server --api-key $API_KEY" },
        { "RAILPACK_START_CMD", "./server --api-key=\"${API_KEY}\" --token \"$TOKEN\"" },
        { "RAILPACK_BUILD_CMD", "DATABASE_PASSWORD=$DB_PASSWORD npm run migrate" },
        { "RAILPACK_BUILD_CMD", "psql postgres://app:$DB_PASSWORD@postgres/app -c 'select 1'" },
    };

    [Theory]
    [MemberData(nameof(Ordinary))]
    public void An_ordinary_setting_never_looks_secret(string key, string value)
    {
        Assert.False(RailpackSettings.IsSecret(key, value, marked: false), value);
        // The Environment tab's row doesn't call it secret either, so its save isn't refused.
        Assert.False(new EnvRowDraft { Key = key, Value = value }.AlwaysSecret, value);
    }

    public static readonly TheoryData<string, string> Secret = new()
    {
        { "RAILPACK_START_CMD", "./server --api-key=s3cr3t-api-key" },
        { "RAILPACK_START_CMD", "./server --token abc123def456" },
        { "RAILPACK_BUILD_CMD", "psql postgres://app:hunter22@postgres/app -c 'select 1'" },
        { "RAILPACK_BUILD_CMD", "DATABASE_PASSWORD=hunter22 npm run migrate" },
        // A $ the shell never expands, inside '…' or after a backslash, is part of a value written out, not a reference.
        { "RAILPACK_START_CMD", "./server --token '$TOKEN'" },
        { "RAILPACK_START_CMD", "./server --db-password '$Ecret99'" },
        { "RAILPACK_START_CMD", "./server --token \\$ecret99" },
        { "RAILPACK_BUILD_CMD", "psql 'postgres://app:$Ecret99@postgres/app' -c 'select 1'" },
        { "RAILPACK_BUILD_CMD", "DATABASE_PASSWORD='$Ecret99' npm run migrate" },
        // A name that looks secret is, whatever its value.
        { "RAILPACK_DEPLOY_TOKEN", "x" },
    };

    [Theory]
    [InlineData("--api-key \"$API_KEY\" --port $PORT", "--api-key  --port ")]
    [InlineData("--api-key=${API_KEY}x", "--api-key=x")]
    [InlineData("--token '$TOKEN' \"it's $X\"", "--token '$TOKEN' \"it's \"")]
    [InlineData("--token \\$TOKEN", "--token \\$TOKEN")]
    [InlineData("--token \"\\$TOKEN\"", "--token \"\\$TOKEN\"")]
    [InlineData("${X:-default} $1 $$ $(cat f)", "${X:-default} $1 $$ $(cat f)")]
    public void Only_the_references_a_shell_expands_are_left_out(string value, string left) =>
        Assert.Equal(left, RailpackSettings.WithoutReferences(value));

    [Fact]
    public void A_secret_the_plan_asks_for_needs_a_variables_name_and_nothing_after_it()
    {
        Assert.True(RailpackSettings.IsVariableName("NPM_TOKEN"));
        Assert.True(RailpackSettings.IsVariableName("_x1"));
        Assert.False(RailpackSettings.IsVariableName("NPM_TOKEN\n"));
        Assert.False(RailpackSettings.IsVariableName("X,src=/data/gitberth.db"));
        Assert.False(RailpackSettings.IsVariableName("1X"));
        Assert.False(RailpackSettings.IsVariableName("my.flag"));
        Assert.False(RailpackSettings.IsVariableName(""));
    }

    [Theory]
    [MemberData(nameof(Secret))]
    public void One_that_holds_a_secret_or_is_marked_so_would_be_secret(string key, string value)
    {
        Assert.True(RailpackSettings.IsSecret(key, value, marked: false), value);
        Assert.True(new EnvRowDraft { Key = key, Value = value }.AlwaysSecret, value);
    }

    [Fact]
    public void Marked_secret_is_secret_and_another_name_never_is_by_this_rule()
    {
        Assert.True(RailpackSettings.IsSecret("RAILPACK_NODE_VERSION", "22", marked: true));
        Assert.True(RailpackSettings.IsSecret(" RAILPACK_NODE_VERSION ", "22", marked: true));
        Assert.False(RailpackSettings.IsSecret("API_KEY", "s3cr3t-api-key", marked: true));
        Assert.False(RailpackSettings.IsSecret("MY_RAILPACK_TOKEN", "x", marked: true));
        // Another variable that names a secret is still secret by Masking's own rule, as before.
        Assert.True(new EnvRowDraft { Key = "SERVER_ARGS", Value = "--token $TOKEN" }.AlwaysSecret);
    }

    [Fact]
    public void The_saves_check_says_which_why_and_how_and_only_for_a_git_app()
    {
        var settings = new ContainerSettings
        {
            Image = "gitberth/shop:1a2b3c4",
            Env =
            [
                new EnvSetting("RAILPACK_START_CMD", "./server --api-key=s3cr3t-api-key"),
                new EnvSetting("RAILPACK_NODE_VERSION", "22", Secret: true),
                new EnvSetting("RAILPACK_BUILD_CMD", "npm run build -- --token $NPM_TOKEN"),
                new EnvSetting("NPM_TOKEN", "tok-9f8e7d6c5b4a", Secret: true),
            ],
        };

        var git = SettingsRules.Check(settings, new SettingsContext(0, 0, new HashSet<string>(), GitApp: true)).Select(p => p.Message).ToList();

        Assert.Equal([RailpackText.SecretSetting("RAILPACK_START_CMD"), RailpackText.SecretSetting("RAILPACK_NODE_VERSION")], git);
        Assert.Equal("RAILPACK_START_CMD can't be secret: Railpack writes its settings into the build plan and the image, so keep the secret in a variable of "
            + "its own and write $NAME in the command.", git[0]);
        // An app that runs an image has no Railpack: its variables are its own business.
        Assert.Empty(SettingsRules.Check(settings, new SettingsContext(0, 0, new HashSet<string>(), GitApp: false)));
    }
}
