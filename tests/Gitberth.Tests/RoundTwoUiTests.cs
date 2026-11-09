using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Components.Pages;
using Gitberth.Components.Shared.Network;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// The words and counts the round-2 review found wrong (L1, L2, L7, L9): a local image's update check said for what it
/// does, one meaning of "stopped" everywhere, the own LAN path's button saying where it goes, and the apply dialog's list
/// changes and kept settings.
/// </summary>
public sealed class RoundTwoUiTests
{
    private static AppSummary App(string name, AppState state = AppState.Running, string? image = null, ImageOrigin origin = ImageOrigin.Local) =>
        new(name, null, name, name, AppSource.Existing, ContainerRole.Other, origin, image ?? $"gitberth-{name}", null, state,
            state.ToString(), "id-" + name, [], null, null, null, false, false, null);

    [Fact]
    public void Stopped_means_not_running_in_the_group_header_and_the_filter_alike()
    {
        List<AppSummary> apps =
        [
            App("sample-node-express"), App("sample-go-chi", AppState.Crashed), App("sample-deno-oak", AppState.Stopped), App("sample-bun-hono", AppState.Created),
        ];

        var group = Assert.Single(AppGrouping.Group(apps, GroupBy.Prefix));

        // The crashed one is stopped and in trouble: the header's "3 stopped · 1 in trouble" is what the filters show.
        Assert.Equal(apps.Count(a => AppFacts.Passes(a, StatusFilter.Stopped)), group.StoppedCount);
        Assert.Equal((1, 3, 1), (group.RunningCount, group.StoppedCount, group.TroubleCount));
    }

    [Fact]
    public void Health_counts_say_which_stopped_containers_it_shows()
    {
        // Every running container, as the header counts them; stopped ones only when it has something of them to show.
        Assert.Equal("70 running · 3 without stats", Gitberth.Components.Pages.Health.ToolbarLine(70, 0, 3));
        Assert.Equal("12 running · 2 stopped in the last 6 hours", Gitberth.Components.Pages.Health.ToolbarLine(12, 2, 0));
    }

    [Fact]
    public void The_local_image_list_says_how_many_more_there_really_are()
    {
        Assert.Equal("Show 2 more", LocalSource.MoreText(2));
        Assert.Equal("Show 40 more", LocalSource.MoreText(40));
        Assert.Equal("Show 40 more of 55", LocalSource.MoreText(55));
    }

    [Fact]
    public void A_local_build_is_watched_here_and_never_asked_of_a_registry()
    {
        Assert.True(AppOverviewTab.BuiltHere(App("sample-node-express", image: "gitberth-sample-node-express")));
        // A local image with a registry's kind of name can still be asked about, and a registry's image isn't built here.
        Assert.False(AppOverviewTab.BuiltHere(App("web", image: "nginx:stable-alpine")));
        Assert.False(AppOverviewTab.BuiltHere(App("db", image: "postgres:18", origin: ImageOrigin.Registry)));

        var upToDate = new UpdateStatus(ImageOrigin.Local, "gitberth-sample-node-express", false, null, null, DateTime.UtcNow,
            "Up to date: it runs the image gitberth-sample-node-express names.", null, false);
        Assert.Equal("Up to date: it runs the newest build of its tag on this host.", AppOverviewTab.UpdateLine(upToDate));
        // Anything else is said as the check said it.
        var newer = upToDate with { Available = true, Text = "A newer local image (built 2 min ago)" };
        Assert.Equal("A newer local image (built 2 min ago)", AppOverviewTab.UpdateLine(newer));
    }

    [Fact]
    public void The_own_lan_path_says_it_leads_to_settings_while_the_host_has_them_off()
    {
        ReachInput Input(bool off) => new()
        {
            Name = "web", Title = "web", Image = "nginx:alpine", LanHost = "10.1.1.80", Ports = [new PortRule(80, "tcp", PortAccess.Lan, 8080, "")], WebPort = 80,
            Networks = [new ReachNetwork("gitberth", ["kanban"], true)], OwnLanPossible = true, OwnLanOff = off,
        };

        Assert.Equal("Set up in Settings", ReachModel.Build(Input(off: true)).Direct.Single(l => l.Action == ReachAction.TurnOnLanAddress).ActionText);
        Assert.Equal("Turn on", ReachModel.Build(Input(off: false)).Direct.Single(l => l.Action == ReachAction.TurnOnLanAddress).ActionText);
    }

    [Fact]
    public void A_list_change_is_shown_item_by_item_never_joined_into_one_value()
    {
        var before = new ContainerSettings { Labels = new Dictionary<string, string> { ["gitberth.sample"] = "node-express", ["tier"] = "web" } };
        var after = before with { Labels = new Dictionary<string, string> { ["gitberth.sample"] = "node-express", ["tier"] = "api", ["team"] = "ops" } };

        var change = ChangeText.ListChange(SettingField.Labels, before, after, []);

        Assert.NotNull(change);
        Assert.Equal(["tier=web"], change.Value.Gone);
        Assert.Equal(["team=ops", "tier=api"], change.Value.New);
        // A secret label's value is masked; a field that isn't a list has no item view.
        var secret = ChangeText.ListChange(SettingField.Labels, before, before with { Labels = new Dictionary<string, string> { ["API_TOKEN"] = "abc123" } }, []);
        Assert.DoesNotContain(secret!.Value.New, i => i.Contains("abc123", StringComparison.Ordinal));
        Assert.Null(ChangeText.ListChange(SettingField.Image, before, after, []));
    }

    [Fact]
    public void Kept_settings_carry_their_values()
    {
        var loaded = new ContainerSettings
        {
            Restart = new RestartSetting("always"), Command = ["tunnel", "run", "--token", "abc123"], Env = [new EnvSetting("TZ", "UTC"), new EnvSetting("LANG", "C")],
        };

        // A name or command as typed is mono; words about a value aren't.
        Assert.Equal(("Name", "cloudflare-tunnel", true), ChangeText.KeepWithValue("Name", "cloudflare-tunnel", loaded, []));
        Assert.Equal(("Restart policy", "Always", false), ChangeText.KeepWithValue("Restart policy", "cloudflare-tunnel", loaded, []));
        Assert.Equal(("Environment", "2 variables", false), ChangeText.KeepWithValue("Environment", "cloudflare-tunnel", loaded, []));
        Assert.Equal(("Hostname", "Docker's", false), ChangeText.KeepWithValue("Hostname", "cloudflare-tunnel", loaded, []));
        Assert.DoesNotContain("abc123", ChangeText.KeepWithValue("Command", "cloudflare-tunnel", loaded, []).Value);
        // One that says its value already keeps it as it is.
        Assert.Null(ChangeText.KeepWithValue("Network bridge", "cloudflare-tunnel", loaded, []).Value);
    }
}
