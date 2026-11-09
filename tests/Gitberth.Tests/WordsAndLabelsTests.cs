using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Components.Pages;
using Gitberth.Components.Shared;
using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Git;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Configuration;

namespace Gitberth.Tests;

/// <summary>
/// What the dialogs, banners, labels and settings say, said once and the same everywhere: a scheduled restart as it
/// really happens for each kind of app, the change dialog naming the app as every page does, one label per setting,
/// the Settings index by its sections' titles, a sign-in whose password can't be read, a GitHub app's limits stored as its
/// Settings tab keeps them, and GITBERTH_DATA left blank.
/// </summary>
public sealed class WordsAndLabelsTests : IDisposable
{
    private readonly AppsRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task A_scheduled_restart_is_described_as_it_happens_to_each_kind_of_app()
    {
        // Only a Git app is recreated through a deploy (RestartScheduler); an app from a registry gets docker restart.
        await using (var db = _rig.Db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "Whoami", Slug = "whoami", Source = AppSource.Registry });
            await db.SaveChangesAsync();
        }

        var nav = await _rig.Apps.ListNavAsync();

        Assert.True(nav.Single(a => a.Slug == "kanban").Deployed);
        Assert.False(nav.Single(a => a.Slug == "whoami").Deployed);
        Assert.StartsWith("Gitberth recreates the app from its current image with a deploy's checks", RestartScheduleDialog.WhatHappens(recreated: true));
        Assert.Equal("Gitberth restarts it with docker restart, so it's down for a few seconds.", RestartScheduleDialog.WhatHappens(recreated: false));
    }

    [Fact]
    public void The_change_dialog_names_the_app_as_every_page_does()
    {
        var options = new AppOptions { Name = "Adminer" };
        var plan = new SettingsPlan
        {
            Key = "gb-adminer",
            Edit = new SettingsEdit("abc", new ContainerSettings(), new ContainerSettings(), options, options),
            Change = new PortChangePlan { Container = "gb-adminer", Verdict = RecreateVerdict.Yes },
        };

        var name = ChangeDialog.NameOf(plan, title: null);

        Assert.Equal("Adminer", name);
        Assert.Equal("Apply 1 change to Adminer?", ChangeText.Title(ChangeForm.Recreate, name, 1));
        Assert.Equal("Recreate Adminer", ChangeText.Button(ChangeForm.Recreate, name));
        Assert.Equal("Recreating Adminer", ChangeText.Running(ChangeForm.Recreate, name));
        // A title given wins; a plan with no name of its own falls back to the container.
        Assert.Equal("Main database", ChangeDialog.NameOf(plan, "Main database"));
        Assert.Equal("gb-adminer", ChangeDialog.NameOf(plan with { Edit = plan.Edit with { LoadedOptions = new AppOptions() } }, null));
    }

    [Fact]
    public void Each_setting_has_one_label_and_one_set_of_choices()
    {
        Assert.Equal(["Never", "On failure", "Unless stopped", "Always"], AppFacts.RestartOptions.Select(o => o.Text));
        Assert.Equal(["no", "on-failure", "unless-stopped", "always"], AppFacts.RestartOptions.Select(o => o.Value));
        Assert.Equal(["Image default", "Command", "None"], AppFacts.HealthOptions.Select(o => o.Text));
        Assert.Equal([HealthMode.Image, HealthMode.Custom, HealthMode.Disabled], AppFacts.HealthOptions.Select(o => o.Value));

        // The change dialog's lines name each field, and say each value, as the forms do.
        var before = new ContainerSettings { Image = "nginx:1.27", Restart = new RestartSetting("unless-stopped") };
        var after = before with
        {
            WorkingDir = "/srv", Restart = new RestartSetting("on-failure", 3), Resources = new ResourceSetting(PidsLimit: 200),
            Health = new HealthSetting(HealthMode.Disabled),
        };
        var lines = SettingsDiff.Lines(before, after, SettingsDiff.Changed(before, after), [], _ => ApplyKind.Recreate);
        Assert.Equal(
        [
            ("Working directory", "the image's", "/srv"),
            ("Restart policy", "Unless stopped", "On failure, 3 tries"),
            ("Process limit", "none", "200"),
            ("Health check", "Image default", "None"),
        ], lines.Select(l => (l.Label, l.Before, l.After)));
        Assert.Equal("changed the working directory, the restart policy and the process limit",
            SettingsDiff.Summary(before, after with { Health = before.Health }, SettingsDiff.Changed(before, after with { Health = before.Health })));

        var git = new GitOptions("main", null, DockerfileMode.Auto, null, null, AutoDeploy: true, VercelVariables: true);
        Assert.Equal([("Build", "Detect automatically", "Custom Dockerfile"), ("Deploy on push", "on", "off")],
            AppSettingsService.GitLines(git, git with { AutoDeploy = false, DockerfileMode = DockerfileMode.Custom }).Select(l => (l.Label, l.Before, l.After)));
        Assert.Equal(("Gather stats", "on", "off"),
            AppSettingsService.OptionLines(new AppOptions(), new AppOptions { GatherStats = false }).Select(l => (l.Label, l.Before, l.After)).Single());
    }

    [Fact]
    public void The_settings_index_names_each_section_by_its_title()
    {
        Assert.Equal(["GitHub access", "Registries", "Updates", "Domains", "Network", "Own LAN addresses", "Deploys", "Admin password", "System"],
            SettingsPage.Toc.Select(t => t.Text));
        // A message that sends you to a section names one there is.
        Assert.Equal("Own LAN addresses are off. Turn them on in Settings › Own LAN addresses.", LanAddresses.OffText);
    }

    [Fact]
    public void A_sign_in_whose_password_cant_be_read_says_so_whenever_it_was_last_checked()
    {
        var checkedAt = DateTime.UtcNow.AddDays(-3).AddHours(-1);
        var signIn = new RegistryInfo(1, "ghcr.io", "acme", checkedAt, checkedAt);

        Assert.Equal("signed in 3 days ago", SettingsPage.RegistryState(signIn));
        Assert.Equal("needs its password again: enter it below", SettingsPage.RegistryState(signIn with { NeedsSecret = true }));
        Assert.Equal("saved", SettingsPage.RegistryState(signIn with { VerifiedAt = null }));
    }

    [Fact]
    public async Task A_github_apps_limits_are_stored_as_its_settings_tab_keeps_them()
    {
        var apps = new AppService(_rig.Db, _rig.Protector, _rig.Cli, _rig.Settings, _rig.Health, _rig.Domains, _rig.Options, _rig.Restarts, new WatchState());
        // New app's Resources fields: 1.5 GiB, half a CPU at low priority, 200 processes, 256 MiB of shared memory.
        var resources = ContainerSettingsDraft.From(new ContainerSettings());
        (resources.Memory, resources.MemoryUnit, resources.Cpus, resources.CpuShares, resources.PidsLimit, resources.ShmMiB) = (1.5, MemoryUnit.GiB, 0.5, 512, 200, 256);
        var model = new AppSettingsModel { Name = "Shop" };
        GitSource.SetLimits(model, resources);

        Assert.Empty(await apps.ValidateAsync(null, model));
        var id = await apps.CreateAsync(new GitHubRepoRef("acme", "shop"), model, null);

        var app = (await apps.GetAsync(id))!;
        Assert.Equal((1536, 0.5), (app.MemoryLimitMb, app.CpuLimit));
        var stored = GitAppSettings.ToContainer(app, _rig.Protector);
        Assert.Equal(new ResourceSetting(MemoryBytes: 1536L << 20, Cpus: 0.5, CpuShares: 512, PidsLimit: 200), stored.Resources);
        Assert.Equal(256L << 20, stored.ShmSizeBytes);

        // Left empty, nothing is stored; too little memory is refused in the words the Settings tab uses.
        var none = new AppSettingsModel { Name = "Blog" };
        GitSource.SetLimits(none, ContainerSettingsDraft.From(new ContainerSettings()));
        var blog = (await apps.GetAsync(await apps.CreateAsync(new GitHubRepoRef("acme", "blog"), none, null)))!;
        Assert.Equal((null, null, null), (blog.MemoryLimitMb, blog.CpuLimit, blog.RuntimeExtras));
        var tiny = ContainerSettingsDraft.From(new ContainerSettings());
        tiny.Memory = 4;
        var small = new AppSettingsModel { Name = "Tiny" };
        GitSource.SetLimits(small, tiny);
        Assert.Contains("The memory limit must be at least 6 MiB.", await apps.ValidateAsync(null, small));
    }

    [Fact]
    public void A_blank_data_folder_counts_as_unset()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitberth-options");
        GitberthOptions Options(string? data, bool container = false) => GitberthOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GITBERTH_DATA"] = data, ["DOTNET_RUNNING_IN_CONTAINER"] = container ? "true" : null })
            .Build(), root);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "App_Data")), Options("   ").DataDir);
        Assert.Equal(Path.GetFullPath("/data"), Options("", container: true).DataDir);
        Assert.Equal(Path.GetFullPath("/srv/gitberth-data"), Options(" /srv/gitberth-data ").DataDir);
    }
}
