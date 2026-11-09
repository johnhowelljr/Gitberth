using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Safety review of the unified-apps release, item 4 (secrets): where env values, registry credentials and cloudflared's
/// token can reach beyond their encrypted store. A failing test here is a finding; a passing one shows a path is safe.
/// </summary>
public sealed class RevSafetySecretsTests : IDisposable
{
    // Stand-ins; none of them is a real value from the host.
    private const string GhcrPat = "ghp_RevSafetyStandInPat0123456789";
    private const string DbPassword = "Hunter2RevSafety";
    private const string Token = "eyJhIjoiUmV2U2FmZXR5U3RhbmRJbiIsInQiOiJ0dW5uZWwiLCJzIjoic2VjcmV0In0";

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly Security.SecretProtector _protector = ImageKit.Protector();

    public void Dispose() => _db.Dispose();

    private static JsonObject Decode(string header) =>
        JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/'))))!.AsObject();

    // ---- SEC-1: a credential goes with whatever the app's image is now, whatever its registry ----

    [Fact]
    public async Task SEC1_a_named_credential_is_never_sent_for_another_registry()
    {
        var registries = new RegistryService(_db, _protector, _engine);
        var ghcr = await registries.SaveAsync(new RegistryInput(null, "ghcr.io", "octo", GhcrPat));
        Assert.True(ghcr.Ok, ghcr.Message);

        // The app came from ghcr.io with that sign-in; its image was later changed (Settings › Image) to another registry.
        var header = await registries.AuthHeaderAsync(ImageReference.Parse("registry.example.com/tool:2")!, ghcr.Id);

        Assert.True(header is null || (string?)Decode(header)["serveraddress"] == "registry.example.com",
            $"ghcr.io's sign-in goes with a request for registry.example.com: {(header is null ? "" : Decode(header)["serveraddress"])}");
    }

    [Fact]
    public async Task SEC1_the_six_hourly_check_sends_no_credential_to_a_registry_it_isnt_for()
    {
        var registries = new RegistryService(_db, _protector, _engine);
        var ghcr = await registries.SaveAsync(new RegistryInput(null, "ghcr.io", "octo", GhcrPat));
        var id = ImageKit.Id('t');
        _engine.Add(ImageKit.Image(id, ["registry.example.com/tool:2"], ["registry.example.com/tool@" + ImageKit.Digest('t')]));
        _engine.Uses("gb-tool", id, "registry.example.com/tool:2");
        _engine.Distribution["registry.example.com/tool:2"] = () => new JsonObject { ["Descriptor"] = new JsonObject { ["digest"] = ImageKit.Digest('u') } };
        await _db.AddAsync(new AppDefinition
        {
            Name = "tool", Slug = "tool", Source = AppSource.Registry, ImageRef = "registry.example.com/tool:2", RegistryId = ghcr.Id,
            UpdatePolicy = UpdatePolicy.Notify, AutoDeploy = false,
        });
        var updates = new UpdateService(new ImageKit.UpdateSteps(_db), ImageKit.Images(_engine, _db, _protector), _db, NullLogger<UpdateService>.Instance);

        // What UpdateWatcher runs by itself every 15 minutes, nobody touching anything.
        await updates.CheckDueAsync(TimeSpan.FromHours(6), default);

        Assert.Contains("distribution registry.example.com/tool:2", _engine.Calls);
        var sent = Assert.Single(_engine.AuthHeaders);
        Assert.True(sent is null || (string?)Decode(sent)["serveraddress"] == "registry.example.com",
            $"The watcher handed Docker ghcr.io's sign-in ({(sent is null ? "" : Decode(sent)["username"])}) for registry.example.com");
    }

    [Fact]
    public async Task SEC1_safe_without_a_named_credential_only_the_registrys_own_goes()
    {
        var registries = new RegistryService(_db, _protector, _engine);
        await registries.SaveAsync(new RegistryInput(null, "ghcr.io", "octo", GhcrPat));

        Assert.Null(await registries.AuthHeaderAsync(ImageReference.Parse("registry.example.com/tool:2")!));
        Assert.Null(await registries.AuthHeaderAsync(ImageReference.Parse("postgres:18")!));
        Assert.NotNull(await registries.AuthHeaderAsync(ImageReference.Parse("ghcr.io/octo/tool:1")!));
    }

    // ---- SEC-2: a Git app's new variable can't be marked secret, and goes on argv and into the deploy log ----

    private AppDefinition Kanban() => new()
    {
        Id = 5, Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:425df8a",
        EnvVars =
        [
            new EnvVar { Id = 1, Key = "AUTH_SECRET", ProtectedValue = _protector.Protect("auth-secret-stand-in"), IsSecret = true },
            new EnvVar { Id = 2, Key = "NEXTAUTH_URL", ProtectedValue = _protector.Protect("https://kanban.example"), IsSecret = false },
        ],
    };

    [Fact]
    public void SEC2_a_url_with_a_password_added_on_the_Environment_tab_stays_secret_through_the_deploy()
    {
        var app = Kanban();
        var loaded = GitAppSettings.ToContainer(app, _protector);
        var draft = ContainerSettingsDraft.From(loaded);
        const string url = "postgres://kanban:" + DbPassword + "@postgres:5432/kanban";

        // Paste .env (or Add variable): the tab then shows it with the Secret tag and masked (AppEnvironmentTab.razor:159).
        draft.PasteEnv("DATABASE_URL=" + url);
        var row = draft.Env.Single(r => r.Key == "DATABASE_URL");
        Assert.NotEqual(url, ContainerService.MaskValue(row.Key, row.Value));

        // Apply: AppService.SaveSettingsAsync -> GitAppSettings.Apply, as the Git branch of AppSettingsService does.
        var wanted = draft.ToSettings();
        GitAppSettings.Apply(app, wanted, GitAppSettings.Changed(loaded, wanted), _protector);

        // The next deploy (DeployPipeline.cs:101-105, :690): its env, its docker run argv and the line it logs.
        var env = app.EnvVars.OrderBy(v => v.Id).Select(v => new EnvValue(v.Key, _protector.TryUnprotect(v.ProtectedValue), v.IsSecret)).ToList();
        var spec = ContainerSpecFactory.For(app, app.CurrentImage!, env, "gitberth");
        var (args, _) = spec.BuildRunCommand();
        var logged = spec.ToDisplayString();

        var problems = new List<string>();
        if (!app.EnvVars.Single(v => v.Key == "DATABASE_URL").IsSecret) problems.Add("DATABASE_URL was saved with IsSecret = false");
        if (args.Any(a => a.Contains(DbPassword, StringComparison.Ordinal))) problems.Add("the password is in docker run's argv");
        if (logged.Contains(DbPassword, StringComparison.Ordinal)) problems.Add("the password is in the line the deploy log writes");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void SEC2_safe_a_variable_already_marked_secret_keeps_its_mark_when_its_value_is_edited()
    {
        var app = Kanban();
        var loaded = GitAppSettings.ToContainer(app, _protector);
        var draft = ContainerSettingsDraft.From(loaded);
        draft.Env.Single(r => r.Key == "AUTH_SECRET").Value = "a-new-auth-secret";
        var wanted = draft.ToSettings();
        GitAppSettings.Apply(app, wanted, GitAppSettings.Changed(loaded, wanted), _protector);

        Assert.True(app.EnvVars.Single(v => v.Key == "AUTH_SECRET").IsSecret);
        var env = app.EnvVars.OrderBy(v => v.Id).Select(v => new EnvValue(v.Key, _protector.TryUnprotect(v.ProtectedValue), v.IsSecret)).ToList();
        Assert.DoesNotContain("a-new-auth-secret", ContainerSpecFactory.For(app, app.CurrentImage!, env, "gitberth").ToDisplayString(), StringComparison.Ordinal);
    }

    // ---- SEC-3: editing cloudflared's masked command writes the mask in as its token ----

    [Fact(Skip = "SEC-3 is the UI fixer's: ContainerSettingsDraft (Components) maps masked words back. Un-skip at integration.")]
    public void SEC3_editing_the_masked_command_around_cloudflareds_token_keeps_the_token()
    {
        var loaded = new ContainerSettings
        {
            Image = "cloudflare/cloudflared:latest", Command = ["tunnel", "--no-autoupdate", "run", "--token", Token], NetworkMode = "host",
            Restart = new RestartSetting("always"),
        };
        var draft = ContainerSettingsDraft.From(loaded);
        // What the Command field shows (AppSettingsTab.razor:615, MaskedLine) ...
        var shown = ShellLine.Join(ContainerService.MaskArgs(loaded.Command!));
        Assert.DoesNotContain(Token, shown, StringComparison.Ordinal);
        // ... and what one keystroke in it sends back (SetCommand, :617-621): the owner adds a flag.
        draft.Command = shown + " --loglevel debug";
        var wanted = draft.ToSettings();

        // The plan's CHANGE line masks the token on both sides, so it can't show that the token is replaced.
        var line = Assert.Single(SettingsDiff.Lines(loaded, wanted, new HashSet<SettingField> { SettingField.Command }, [], _ => ApplyKind.Recreate));
        Assert.DoesNotContain(Token, line.Before + line.After, StringComparison.Ordinal);

        Assert.Equal(Token, wanted.Command![wanted.Command.ToList().IndexOf("--token") + 1]);
    }

    [Fact]
    public void SEC3_safe_cloudflareds_token_is_masked_in_the_plan_the_docker_run_and_the_summary()
    {
        var loaded = new ContainerSettings
        {
            Image = "cloudflare/cloudflared:latest", Command = ["tunnel", "--no-autoupdate", "run", "--token", Token], NetworkMode = "host",
        };
        var wanted = loaded with { Command = ["tunnel", "run", "--token=" + Token] };
        var fields = SettingsDiff.Changed(loaded, wanted);

        var lines = SettingsDiff.Lines(loaded, wanted, fields, [], _ => ApplyKind.Recreate);
        Assert.All(lines, l => Assert.DoesNotContain(Token, l.Before + l.After, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, SettingsDiff.Summary(loaded, wanted, fields), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ContainerSettingsJson.DockerRun("cloudflare-tunnel", wanted, [], []), StringComparison.Ordinal);
    }

    // ---- SEC-4: the masking rules disagree with each other ----

    [Theory]
    [InlineData("redis-server", "--requirepass", "Hunter2RevSafety")]
    [InlineData("sh", "-c", "cloudflared tunnel run --token Hunter2RevSafety")]
    public void SEC4_a_command_line_secret_is_masked_in_the_plan(string program, string option, string value)
    {
        var loaded = new ContainerSettings { Image = "x:1", Command = [program] };
        var wanted = loaded with { Command = [program, option, value] };

        var line = Assert.Single(SettingsDiff.Lines(loaded, wanted, new HashSet<SettingField> { SettingField.Command }, [], _ => ApplyKind.Recreate));

        Assert.DoesNotContain(DbPassword, line.After, StringComparison.Ordinal);
    }

    [Fact]
    public void SEC4_a_url_password_with_a_slash_is_secret_everywhere_the_screen_says_it_is()
    {
        // A generated base64 password often has a slash. The Environment tab masks it (MaskValue's pattern allows one) ...
        const string url = "postgres://app:Ab3/x9+QHunter2@postgres:5432/app";
        Assert.NotEqual(url, ContainerService.MaskValue("DATABASE_URL", url));
        // ... but the rule the secret flag, the scrubber, the docker run line, the Compose snippet and the deploy's GitHub
        // variables use (EnvRules.HasCredentials) doesn't see a password in it.
        Assert.True(EnvRules.HasCredentials(url), "HasCredentials misses a URL password with a '/' in it");
    }
}
