using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Components.Shared.Network;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Sources;

namespace Gitberth.Tests;

/// <summary>
/// New app's Configure step after the review: the App page's own sections in create mode (labels, logging, Advanced,
/// resources) carried through the form, the network mode, networks a paste names that the host doesn't have, a failed
/// create said truthfully, and the paste's words read back as they should be.
/// </summary>
public sealed class NewAppFormTests
{
    private static NewAppForm Pasted(string text)
    {
        var parsed = DockerRunParser.Parse(text);
        var draft = new NewAppDraft
        {
            Source = AppSource.Manual, Name = parsed.Name ?? NewAppText.NameFor(parsed.Settings.Image), Settings = parsed.Settings,
            ExtraNetworks = parsed.Networks, LanAddress = parsed.LanAddress, Issues = parsed.Issues,
        };
        return NewAppForm.From(draft, null, pasted: true);
    }

    [Fact]
    public void A_pasted_host_network_app_keeps_its_mode_privileged_and_folder_through_the_form()
    {
        var form = Pasted("docker run --network host --privileged -v /srv/ha:/config ghcr.io/home-assistant/home-assistant:stable");

        Assert.Equal("host", form.NetworkMode);
        Assert.False(form.OnNetworks);
        Assert.True(form.Pasted);
        Assert.True(form.More.Privileged);
        Assert.Contains("Privileged", SettingsAdvanced.Set(form.More.Loaded));

        var back = form.ToDraft().Settings;
        Assert.Equal("host", back.NetworkMode);
        Assert.True(back.Privileged);
        Assert.Equal(new MountSetting(MountKind.Bind, "/srv/ha", "/config", false), back.Mounts.Single());

        // Choosing its own networks again in Configure is a change of the draft's network mode.
        form.NetworkMode = "";
        Assert.True(form.OnNetworks);
        Assert.Equal("", form.ToDraft().Settings.NetworkMode);
    }

    [Fact]
    public void The_settings_sections_edit_in_create_mode_and_reach_the_draft()
    {
        var form = Pasted("docker run -d --name web -l com.example.team=ops --log-driver json-file -p 8080:80 nginx:alpine");

        var label = form.More.AddLabel();
        label.Key = "traefik.enable";
        label.Value = "true";
        form.More.RemoveLabel(form.More.Labels.Single(l => l.Key == "com.example.team"));
        form.More.LogDriver = "local";
        form.More.LogOptions = "max-size=10m";
        form.More.Hostname = "web-1";
        form.More.Devices = "/dev/dri:/dev/dri";
        form.More.CapAdd = "NET_ADMIN";
        form.More.Init = true;
        form.More.StopTimeoutSeconds = 30;
        form.More.ShmMiB = 256;
        form.More.PidsLimit = 200;
        form.More.CpuShares = 512;
        form.MemoryMb = 512;
        form.Cpus = 1.5;

        var s = form.ToDraft().Settings;
        Assert.Equal("true", s.Labels["traefik.enable"]);
        Assert.False(s.Labels.ContainsKey("com.example.team"));
        Assert.Equal("local", s.Logging.Driver);
        Assert.Equal("10m", s.Logging.Options!["max-size"]);
        Assert.Equal("web-1", s.Hostname);
        Assert.Equal([new DeviceSetting("/dev/dri", "/dev/dri")], s.Devices);
        Assert.Equal(["NET_ADMIN"], s.CapAdd);
        Assert.True(s.Init);
        Assert.Equal(30, s.StopTimeoutSeconds);
        Assert.Equal(256L * 1024 * 1024, s.ShmSizeBytes);
        Assert.Equal((200L, 512L, 512L * 1024 * 1024, 1.5), (s.Resources.PidsLimit!.Value, s.Resources.CpuShares!.Value, s.Resources.MemoryBytes!.Value, s.Resources.Cpus!.Value));
        // The form's own fields still win: the ports are the Ports section's.
        Assert.Equal((80, PortAccess.Lan, 8080), (s.Ports.Single().ContainerPort, s.Ports.Single().Access, s.Ports.Single().HostPort!.Value));
        Assert.Equal((512L, 1.5), (form.MemoryMb!.Value, form.Cpus!.Value));
    }

    [Fact]
    public void Advanced_opens_only_when_the_source_set_something_in_it()
    {
        Assert.Empty(SettingsAdvanced.Set(new ContainerSettings { Image = "nginx" }));
        Assert.Equal(["Hostname", "Init", "Devices"], SettingsAdvanced.Set(new ContainerSettings
        {
            Hostname = "a", Init = true, Devices = [new DeviceSetting("/dev/x", "/dev/x")],
        }));
    }

    [Fact]
    public void A_network_the_paste_names_that_isnt_on_the_host_is_listed_to_create_or_remove()
    {
        var run = Pasted("docker run -d --name t --network mynet -p 8080:80 nginx:alpine");
        Assert.Equal(["mynet"], run.ExtraNetworks);
        Assert.Equal(["mynet"], run.Missing(["gitberth", "bridge", "media"], "gitberth"));
        Assert.Empty(run.Missing(["gitberth", "mynet"], "gitberth"));

        var compose = ComposeParser.Parse("services:\n  web:\n    image: nginx:alpine\n    networks: [front]\n", "web");
        var form = NewAppForm.From(new NewAppDraft { Source = AppSource.Manual, Name = "web", Settings = compose.Settings, ExtraNetworks = compose.Networks }, null, pasted: true);
        Assert.Equal(["front"], form.Missing(["gitberth"], "gitberth"));

        // Taken out, it's gone from the draft too.
        form.ExtraNetworks.Remove("front");
        Assert.DoesNotContain(form.ToDraft().ExtraNetworks, n => n.Name == "front");
    }

    [Fact]
    public void The_shared_networks_aliases_from_a_paste_are_carried()
    {
        var draft = new NewAppDraft
        {
            Source = AppSource.Manual, Name = "web", Settings = new ContainerSettings { Image = "nginx" },
            ExtraNetworks = [new NetworkJoin("", ["www"]), new NetworkJoin("media", ["web"])],
        };
        var back = NewAppForm.From(draft, null).ToDraft();
        Assert.Equal([("", "www"), ("media", "web")], back.ExtraNetworks.Select(n => (n.Name, n.Aliases.Single())));
    }

    [Fact]
    public void On_the_hosts_network_the_ports_can_be_taken_out()
    {
        var form = Pasted("docker run -d -p 8080:80 nginx:alpine");
        form.NetworkMode = "host";
        Assert.NotEmpty(form.Ports.Rows);
        form.TakePortsOut();
        Assert.Empty(form.Ports.Rows);
        Assert.Empty(form.ToDraft().Settings.Ports);
    }

    [Fact]
    public void Everything_else_lists_only_what_has_no_field()
    {
        var form = NewAppForm.From(new NewAppDraft
        {
            Source = AppSource.Manual, Name = "x",
            Settings = new ContainerSettings
            {
                Image = "nginx", Hostname = "h", Privileged = true, DnsSearch = ["lan"], OpenStdin = true,
                Labels = new Dictionary<string, string> { ["a"] = "b" },
            },
        }, null);
        Assert.Equal(["DNS search", "Input"], form.Unfielded().Select(u => u.Name));
    }

    [Fact]
    public void Gitberth_read_keeps_udp_and_says_the_network_mode()
    {
        Assert.Equal("127.0.0.1:5353:53/udp (Host only)", NewAppText.PortText(new PortRule(53, "udp", PortAccess.HostOnly, 5353)));
        Assert.Equal("8080:80 (LAN)", NewAppText.PortText(new PortRule(80, "tcp", PortAccess.Lan, 8080)));
        Assert.Equal("5353:53/udp (LAN)", NewAppText.PortText(new PortRule(53, "udp", PortAccess.Lan, 5353)));
        Assert.Equal("5432/tcp (Internal)", NewAppText.PortText(new PortRule(5432, "tcp", PortAccess.Internal)));

        Assert.Null(NewAppText.NetworkModeText(""));
        Assert.Equal("The host's own", NewAppText.NetworkModeText("host"));
        Assert.Equal("None", NewAppText.NetworkModeText("none"));
        Assert.StartsWith("vpn's", NewAppText.NetworkModeText("container:vpn"));
    }

    [Fact]
    public void A_dropped_line_doesnt_repeat_its_heading_and_names_the_github_card()
    {
        var dropped = new ParseIssue(ParseSeverity.Dropped, "Not carried over: the published ports: with --network host it has no ports of its own.", "-p");
        Assert.Equal("The published ports: with --network host it has no ports of its own.", ParseIssueView.TextOf(dropped));
        var note = new ParseIssue(ParseSeverity.Note, "It has build: as well. To build it from its code, use From GitHub.", "build");
        Assert.Equal("It has build: as well. To build it from its code, use the GitHub repository source.", ParseIssueView.TextOf(note));
        Assert.Equal("GitHub repository", NewAppSources.Of(NewAppSource.Git).Title);
        var compose = new ParseIssue(ParseSeverity.Note, "Compose would make the network as app_front; Gitberth joins one called front, which must exist (Network page).", "networks: front");
        Assert.EndsWith("which must exist: Configure offers to create it.", ParseIssueView.TextOf(compose));
    }

    [Fact]
    public void A_failed_create_says_what_it_left_and_never_that_nothing_happens_until_create()
    {
        Assert.Equal("One last look. Nothing is created until you press Create.", NewAppText.CreateLead(false, null, false));
        Assert.StartsWith("Creating it now", NewAppText.CreateLead(true, null, false));
        var failed = NewAppText.CreateLead(true, false, false);
        Assert.DoesNotContain("until you press Create", failed);
        Assert.Contains("nothing was left behind", failed);
        Assert.Contains("kept", NewAppText.CreateLead(true, false, true));

        Assert.Equal("Nothing was left behind: no gb-redis container and no app record, and its host ports are free again.", NewAppText.LeftBehind("gb-redis", null));
        Assert.Contains("gb-redis", NewAppText.LeftBehind("gb-redis", "gb-redis"));
    }

    [Fact]
    public void The_review_says_how_to_fix_a_missing_network_here()
    {
        SettingProblem[] problems =
        [
            new(SettingField.NetworkMode, "There's no network called mynet: make it on the Network page, or take it out."),
            new(SettingField.Ports, "Port 8080 is used by kanban."),
        ];
        var lines = NewAppText.ReviewProblems(problems, ["mynet"]);
        Assert.Equal(2, lines.Count);
        Assert.Contains("create it or take it out under Network, in Configure", lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains("Network page"));
        Assert.Equal("Port 8080 is used by kanban.", lines[1]);
    }

    [Fact]
    public void An_internal_port_on_the_default_bridge_says_nothing_reaches_it_by_name()
    {
        Assert.Equal("Not published. Containers on media reach it by name.", PortsTable.InternalTitle(true, "media", "gitberth"));
        Assert.Equal("Not published. Containers on gitberth reach it by name.", PortsTable.InternalTitle(true, null, "gitberth"));
        Assert.Equal("Not published. Nothing reaches it by name until it joins gitberth.", PortsTable.InternalTitle(false, null, "gitberth"));
    }
}
