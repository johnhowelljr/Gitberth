using Gitberth.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Components.Shared.Network;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Sources;

namespace Gitberth.Tests;

/// <summary>
/// The change dialog's words and how it lays a plan out (KEEPS as chips, IMPACT by what it says), New app's view logic (a
/// paste's issues as the page groups them, the form made from a source's draft and back, the consequence line), and the
/// line under Settings › Own LAN addresses' Block field when the host's network can't be read.
/// </summary>
public sealed class ChangeDialogAndNewAppTests
{
    private static PortChangePlan Change() => new() { Container = "postgres", Verdict = RecreateVerdict.Yes };

    private static SettingsPlan Plan(PortChangePlan? change = null, string? compose = null, params SettingLine[] lines) => new()
    {
        Key = "postgres",
        Edit = new SettingsEdit("abc", new ContainerSettings(), new ContainerSettings(), new AppOptions(), new AppOptions()),
        Change = change,
        ComposeSnippet = compose,
        Lines = lines,
    };

    [Fact]
    public void Keeps_reads_as_chips_with_brackets_kept_whole()
    {
        var chips = ChangeText.Keeps("Its name, image postgres:18 (3f1c2b9a04d1, not pulled again), command, environment, volumes (postgres-data and 1 anonymous), "
            + "network gitberth (names postgres), restart policy, limits, health check and hostname.");

        Assert.Equal(["Name", "Image postgres:18 (3f1c2b9a04d1, not pulled again)", "Command", "Environment", "Volumes (postgres-data and 1 anonymous)",
            "Network gitberth (names postgres)", "Restart policy", "Limits", "Health check", "Hostname"], chips);
        Assert.Equal(["Name"], ChangeText.Keeps("Its name."));
        Assert.Empty(ChangeText.Keeps(""));
    }

    [Fact]
    public void The_dialog_takes_the_form_of_what_the_plan_does()
    {
        Assert.Equal(ChangeForm.Recreate, ChangeText.FormOf(Plan(Change())));
        Assert.Equal(ChangeForm.Compose, ChangeText.FormOf(Plan(Change(), compose: "services: {}")));
        Assert.Equal(ChangeForm.Save, ChangeText.FormOf(Plan()));

        Assert.Equal("Apply 3 changes to postgres?", ChangeText.Title(ChangeForm.Recreate, "postgres", 3));
        Assert.Equal("Apply 1 change to postgres?", ChangeText.Title(ChangeForm.Live, "postgres", 1));
        Assert.Equal("Update postgres?", ChangeText.Title(ChangeForm.Update, "postgres", 1));
        Assert.Equal(("Recreate postgres", "Apply, no restart", "Roll back postgres"),
            (ChangeText.Button(ChangeForm.Recreate, "postgres"), ChangeText.Button(ChangeForm.Live, "postgres"), ChangeText.Button(ChangeForm.Rollback, "postgres")));
        Assert.Contains("No restart", ChangeText.Sub(ChangeForm.Live, "postgres", running: true));
        Assert.StartsWith("postgres is stopped", ChangeText.Sub(ChangeForm.Recreate, "postgres", running: false));
    }

    [Fact]
    public void Ports_lines_count_once_with_the_settings_lines()
    {
        var ports = Change() with { Changes = [new PortChangeLine("5432/tcp", "Internal", "LAN at 10.1.1.80:5432")] };
        var plan = Plan(ports, null,
            new SettingLine(SettingField.Ports, "Ports", "5432/tcp", "10.1.1.80:5432", ApplyKind.Recreate),
            new SettingLine(SettingField.Memory, "Memory limit", "No limit", "1 GiB", ApplyKind.Live));

        // The port's own line stands for the Ports field, so it isn't counted twice.
        Assert.Equal(2, ChangeText.Count(plan));
        Assert.Equal(1, ChangeText.Count(Plan()));
    }

    [Fact]
    public void Impact_lines_are_drawn_by_what_they_say()
    {
        Assert.Equal("warn", ChangeText.ImpactLook("postgres restarts once: about 5 s offline.").Tone);
        Assert.Equal("neutral", ChangeText.ImpactLook("kanban uses postgres: its connections drop and reconnect.").Tone);
        Assert.Equal("good", ChangeText.ImpactLook("If the new container fails its checks, the old one comes back as it was.").Tone);
        Assert.Equal("serious", ChangeText.ImpactLook("2 files changed outside its volumes are lost: /etc/hosts.allow, /root/.bashrc.").Tone);
        Assert.Equal("warn", ChangeText.ImpactLook("Every domain it serves stops answering until it's back.").Tone);
    }

    [Fact]
    public void CONC1_a_job_for_another_plan_is_refused_never_followed_as_this_change()
    {
        var mine = Change();
        var theirs = Change();
        Assert.Null(ChangeText.NotThisChange(new PortChangeJob(mine), mine, "postgres"));
        Assert.Equal("Gitberth is already changing postgres. Try again when that's done.", ChangeText.NotThisChange(new PortChangeJob(theirs), mine, "postgres"));
        // A job handed to the dialog to follow has no plan of the dialog's own to compare.
        Assert.Null(ChangeText.NotThisChange(new PortChangeJob(theirs), null, "postgres"));
    }

    [Fact]
    public void Progress_says_which_step_and_how_long()
    {
        Assert.Equal("Step 6 of 9 · 1.9 s so far", ChangeText.Progress(new PortChangeStep(6, 9, "Starting", StepState.Running), 5, 9, TimeSpan.FromSeconds(1.9)));
        Assert.Equal("Step 3 of 9 · 0.4 s so far", ChangeText.Progress(null, 2, 9, TimeSpan.FromSeconds(0.4)));
    }

    [Fact]
    public void A_pastes_issues_are_grouped_errors_first_and_errors_block()
    {
        var view = ParseIssueView.Of(
        [
            new ParseIssue(ParseSeverity.Note, "Gitberth names it gb-web; other containers reach it as web", "--name web"),
            new ParseIssue(ParseSeverity.Dropped, "Gitberth keeps its apps", "--rm"),
            new ParseIssue(ParseSeverity.Error, "Give DB_PASSWORD a value", "-e DB_PASSWORD"),
            new ParseIssue(ParseSeverity.Dropped, "Join a network instead", "--link db"),
        ]);

        Assert.True(view.Blocks);
        Assert.Equal((1, 2, 1), (view.Errors.Count, view.Dropped.Count, view.Notes.Count));
        Assert.Equal("1 thing to fix before it can be created", ParseIssueView.Heading(ParseSeverity.Error, 1));
        Assert.Equal("Not carried over (2)", ParseIssueView.Heading(ParseSeverity.Dropped, 2));
        Assert.Equal("3 notes on how Gitberth read it", ParseIssueView.Heading(ParseSeverity.Note, 3));
        Assert.False(ParseIssueView.Of([new ParseIssue(ParseSeverity.Dropped, "Gitberth keeps its apps", "--rm")]).Blocks);
    }

    [Fact]
    public void The_new_app_form_round_trips_a_draft_and_leaves_the_images_own_alone()
    {
        var facts = new ImageFacts("sha256:1", ["postgres:18"], [], null, 0, "linux", "amd64",
            [new EnvSetting("PATH", "/usr/bin", true), new EnvSetting("PG_VERSION", "18.0", true)], null, ["postgres"], null, null,
            [new ExposedPort(5432, "tcp")], ["/var/lib/postgresql"], new Dictionary<string, string>(), new HealthSetting(), null);
        var draft = new NewAppDraft
        {
            Source = AppSource.Registry, Name = "postgres", RegistryId = 3,
            Settings = new ContainerSettings
            {
                Image = "postgres:18",
                Env = [new EnvSetting("PATH", "/usr/bin", true), new EnvSetting("PG_VERSION", "18.0", true), new EnvSetting("POSTGRES_PASSWORD", "x", Secret: true),
                    new EnvSetting("DATABASE_URL", "postgres://app:hunter2@db/app")],
                Ports = [new PortRule(5432, "tcp", PortAccess.Lan, 5433)],
                Mounts = [new MountSetting(MountKind.Volume, "gb-postgres-postgresql", "/var/lib/postgresql")],
                Labels = new Dictionary<string, string> { ["com.example"] = "kept" },
            },
            ExtraNetworks = [new NetworkJoin("media", ["db"])],
            Options = new AppOptions { WebPort = null },
        };

        var form = NewAppForm.From(draft, facts);
        Assert.Equal((2, 2), (form.Env.Count(e => e.FromImage), form.OwnEnv.Count()));
        // A value with a password in a URL is masked like a secret-named one.
        Assert.True(form.Env.Single(e => e.Key == "DATABASE_URL").Secret);
        Assert.True(form.Mounts.Single().FromImage);
        Assert.Equal(["5432/tcp"], form.Ports.ImageExposed);

        form.Name = "  pg ";
        form.MemoryMb = 512;
        form.Restart = "on-failure";
        form.Retries = 3;
        form.Env.Single(e => e.Key == "PG_VERSION").Value = "18.1";
        form.Ports.SetWeb(null);
        var back = form.ToDraft();

        Assert.Equal(("pg", "pg", 3), (back.Name, back.Options.Name, back.RegistryId));
        Assert.Equal(512L * 1024 * 1024, back.Settings.Resources.MemoryBytes);
        Assert.Equal(new RestartSetting("on-failure", 3), back.Settings.Restart);
        // The image's PATH stays the image's; the edited PG_VERSION becomes the container's own.
        Assert.True(back.Settings.Env.Single(e => e.Key == "PATH").FromImage);
        Assert.False(back.Settings.Env.Single(e => e.Key == "PG_VERSION").FromImage);
        Assert.Equal(["POSTGRES_PASSWORD", "DATABASE_URL"], back.Options.SecretKeys);
        Assert.Equal(new PortRule(5432, "tcp", PortAccess.Lan, 5433, null), back.Settings.Ports.Single());
        Assert.Equal("media", back.ExtraNetworks.Single().Name);
        Assert.Equal(["db"], back.ExtraNetworks.Single().Aliases);
        Assert.Equal("kept", back.Settings.Labels["com.example"]);
        Assert.Null(back.Settings.Command);
    }

    [Fact]
    public void The_consequence_line_says_what_create_does_and_where_it_answers()
    {
        Assert.Equal("Creates gb-uptime-kuma and starts it. Reached at 10.1.1.80:3001.", NewAppText.Consequence("gb-uptime-kuma", "10.1.1.80:3001", false, null));
        Assert.Equal("Pulls redis:7-alpine, then creates gb-redis and starts it.", NewAppText.Consequence("gb-redis", null, true, "redis:7-alpine"));

        PortRule[] ports = [new PortRule(9229, "tcp", PortAccess.Lan, 9229), new PortRule(3000, "tcp", PortAccess.HostOnly, 3002)];
        Assert.Equal("127.0.0.1:3002", NewAppText.FirstAddress(ports, webPort: 3000, "10.1.1.80", "app"));
        Assert.Equal("10.1.1.80:9229", NewAppText.FirstAddress(ports, webPort: null, "10.1.1.80", "app"));
        Assert.Equal("worker:8080", NewAppText.FirstAddress([new PortRule(8080, "tcp", PortAccess.Internal)], null, "10.1.1.80", "worker"));
        Assert.Null(NewAppText.FirstAddress([], null, "10.1.1.80", "worker"));
    }

    [Fact]
    public void Names_sizes_and_ages_read_the_way_people_write_them()
    {
        Assert.Equal(("postgres", "uptime-kuma", "app"), (NewAppText.NameFor("docker.io/library/postgres:18"), NewAppText.NameFor("louislam/uptime-kuma:1"),
            NewAppText.NameFor("ghcr.io/o/app@sha256:abc")));
        Assert.Equal(("57.8 MB", "1.2 GB", "800 kB"), (NewAppText.Size(57_800_000), NewAppText.Size(1_200_000_000), NewAppText.Size(800_000)));
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(("2 days ago", "2 weeks ago", "4 months ago"), (NewAppText.Ago(now.AddDays(-2), now), NewAppText.Ago(now.AddDays(-20), now), NewAppText.Ago(now.AddDays(-125), now)));
    }

    [Fact]
    public void The_block_help_never_leaves_a_gap_when_the_network_cant_be_read()
    {
        // The bug: "A /24 to a /30, like , or a range…" when the host's network couldn't be read.
        var unread = LanBlockText.Help(null, "", null, null);
        Assert.DoesNotContain("like ,", unread);
        Assert.Contains(LanBlockText.GenericExample, unread);
        Assert.Equal("A /24 to a /30, like 10.1.1.224/28, or a range of 4 to 256 addresses.", LanBlockText.Help("", "10.1.1.224/28", null, null));
        Assert.Equal("16 addresses", LanBlockText.Help("10.1.1.224/28", "10.1.1.224/28", "16 addresses", null));
        Assert.Equal("It overlaps gitberth.", LanBlockText.Help("172.21.0.0/28", "10.1.1.224/28", null, "It overlaps gitberth."));
        Assert.Equal("Gitberth can check it once it can read this host's network.", LanBlockText.Help("10.1.1.224/28", null, null, null));
    }
}
