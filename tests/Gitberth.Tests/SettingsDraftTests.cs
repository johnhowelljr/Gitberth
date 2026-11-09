using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Data;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// The Settings and Environment tabs' drafts: what changed against what was loaded, the settings they ask for (the
/// SettingsEdit's Wanted), and that nothing the form didn't touch moves. postgres runs postgres:18 with its own
/// POSTGRES_PASSWORD and the image's PATH and PG_VERSION, a named volume, unless-stopped, and no limits.
/// </summary>
public sealed class SettingsDraftTests
{
    private static ContainerSettings Postgres() => new()
    {
        Image = "postgres:18",
        Env =
        [
            new("POSTGRES_PASSWORD", "s3cret", Secret: true),
            new("PATH", "/usr/local/sbin:/usr/local/bin:/usr/lib/postgresql/18/bin", FromImage: true),
            new("PG_VERSION", "18.0-1.pgdg13+3", FromImage: true),
        ],
        Mounts = [new(MountKind.Volume, "postgres-data", "/var/lib/postgresql")],
        Restart = new("unless-stopped"),
        Labels = new Dictionary<string, string> { ["gitberth.app"] = "postgres", ["owner"] = "me" },
        Hostname = "3a4264520e12",
        ShmSizeBytes = 64L * 1024 * 1024,
        Logging = new LogSetting("journald"),
    };

    [Fact]
    public void Nothing_edited_is_no_change_and_asks_for_exactly_what_was_loaded()
    {
        var loaded = Postgres();
        var draft = ContainerSettingsDraft.From(loaded);
        Assert.Empty(draft.ChangedFields());
        Assert.False(draft.Dirty);

        var wanted = draft.ToSettings();
        // Every list and map is the loaded one itself: nothing is rebuilt that wasn't edited.
        Assert.Same(loaded.Env, wanted.Env);
        Assert.Same(loaded.Mounts, wanted.Mounts);
        Assert.Same(loaded.Labels, wanted.Labels);
        Assert.Same(loaded.Restart, wanted.Restart);
        Assert.Same(loaded.Resources, wanted.Resources);
        Assert.Same(loaded.Health, wanted.Health);
        Assert.Same(loaded.Logging, wanted.Logging);
        Assert.Equal(loaded.Hostname, wanted.Hostname);
        Assert.Equal(loaded.ShmSizeBytes, wanted.ShmSizeBytes);
    }

    [Fact]
    public void Typing_a_value_and_typing_it_back_is_no_change()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.Env[0].Value = "other";
        draft.Image = "postgres:19";
        Assert.Equal([SettingField.Image, SettingField.Env], draft.ChangedFields().Order());

        draft.Env[0].Value = "s3cret";
        draft.Image = "postgres:18";
        Assert.Empty(draft.ChangedFields());
    }

    [Fact]
    public void The_images_variables_pass_on_untouched_until_one_is_edited()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.AddEnv("TZ", "Europe/London");
        var wanted = draft.ToSettings();
        Assert.Equal(["POSTGRES_PASSWORD", "PATH", "PG_VERSION", "TZ"], wanted.Env.Select(e => e.Key));
        Assert.True(wanted.Env[1].FromImage);
        Assert.True(wanted.Env[2].FromImage);
        Assert.False(wanted.Env[3].FromImage);

        // Editing one makes it the container's own.
        draft.Env[2].Value = "18.1";
        wanted = draft.ToSettings();
        Assert.Equal(new EnvSetting("PG_VERSION", "18.1"), wanted.Env[2]);
        Assert.True(wanted.Env[1].FromImage);
    }

    [Fact]
    public void A_removed_variable_stays_until_applied_and_undo_brings_it_back()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        var password = draft.Env[0];
        draft.RemoveEnv(password);
        Assert.Contains(password, draft.Env);
        Assert.True(password.Removed);
        Assert.DoesNotContain(draft.ToSettings().Env, e => e.Key == "POSTGRES_PASSWORD");

        password.Undo();
        Assert.Empty(draft.ChangedFields());

        // A row added and taken away again leaves nothing behind.
        var added = draft.AddEnv("X", "1");
        draft.RemoveEnv(added);
        Assert.DoesNotContain(added, draft.Env);
        Assert.Empty(draft.ChangedFields());
    }

    [Fact]
    public void An_empty_new_row_is_no_change()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.AddEnv();
        Assert.False(draft.EnvChanged);
        Assert.Empty(draft.ChangedFields());
        Assert.Equal(3, draft.ToSettings().Env.Count);
    }

    [Fact]
    public void Pasting_a_dotenv_file_replaces_by_name_and_adds_the_rest()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        var read = draft.PasteEnv("# a comment\r\nexport TZ=\"Europe/London\"\nPOSTGRES_PASSWORD=new\n\nnot a line\nAPI_TOKEN='abc'\n");
        Assert.Equal(3, read);
        var env = draft.ToSettings().Env;
        Assert.Equal("new", env.Single(e => e.Key == "POSTGRES_PASSWORD").Value);
        Assert.Equal("Europe/London", env.Single(e => e.Key == "TZ").Value);
        Assert.Equal("abc", env.Single(e => e.Key == "API_TOKEN").Value);
        // A name that looks secret is masked from the start.
        Assert.True(draft.Env.Single(r => r.Key == "API_TOKEN").Secret);
    }

    [Fact]
    public void Says_what_is_wrong_with_the_variables()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.AddEnv("TZ", "a");
        draft.AddEnv("TZ", "b");
        draft.AddEnv("", "orphan");
        draft.AddEnv("BAD NAME", "x");
        var problems = draft.EnvProblems();
        Assert.Contains("TZ is set twice.", problems);
        Assert.Contains("A variable has a value but no name.", problems);
        Assert.Contains(problems, p => p.Contains("BAD NAME", StringComparison.Ordinal));
    }

    [Fact]
    public void Memory_is_asked_for_in_its_unit_and_a_loaded_odd_value_isnt_rounded_into_a_change()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        Assert.Null(draft.Memory);
        draft.Memory = 1;
        draft.MemoryUnit = MemoryUnit.GiB;
        Assert.Equal(1L << 30, draft.ToSettings().Resources.MemoryBytes);
        Assert.Contains(SettingField.Memory, draft.ChangedFields());
        draft.MemoryUnit = MemoryUnit.MiB;
        draft.Memory = 512;
        Assert.Equal(512L << 20, draft.ToSettings().Resources.MemoryBytes);

        // 100 000 000 bytes shows as 95.4 MiB; untouched, it stays exactly as Docker has it.
        var odd = ContainerSettingsDraft.From(Postgres() with { Resources = new ResourceSetting(MemoryBytes: 100_000_000) });
        Assert.Equal((95.4, MemoryUnit.MiB), (odd.Memory, odd.MemoryUnit));
        Assert.Equal(100_000_000, odd.ToSettings().Resources.MemoryBytes);
        Assert.Empty(odd.ChangedFields());

        Assert.Equal((1.5, MemoryUnit.GiB), ContainerSettingsDraft.MemoryOf(3L << 29));
    }

    [Fact]
    public void Each_live_field_is_its_own_change()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.RestartPolicy = "always";
        draft.Cpus = 2;
        draft.PidsLimit = 200;
        Assert.Equal([SettingField.RestartPolicy, SettingField.Cpu, SettingField.PidsLimit], draft.ChangedFields().Order());
        Assert.True(draft.ChangedFields().All(SettingFields.Live.Contains));
        Assert.Equal(new RestartSetting("always"), draft.ToSettings().Restart);

        draft.UndoRestart();
        draft.UndoCpu();
        draft.PidsLimit = null;
        Assert.Empty(draft.ChangedFields());
    }

    [Fact]
    public void A_restart_policy_docker_left_empty_reads_as_never()
    {
        var draft = ContainerSettingsDraft.From(Postgres() with { Restart = new RestartSetting("") });
        Assert.Equal("no", draft.RestartPolicy);
        Assert.Empty(draft.ChangedFields());
    }

    [Fact]
    public void The_command_is_a_command_line_and_empty_means_the_images()
    {
        var loaded = Postgres() with { Command = ["tunnel", "--no-autoupdate", "run", "--token", "abc def"] };
        var draft = ContainerSettingsDraft.From(loaded);
        Assert.Equal("tunnel --no-autoupdate run --token \"abc def\"", draft.Command);
        Assert.Same(loaded.Command, draft.ToSettings().Command);

        draft.Command = "postgres -c 'max_connections=200'";
        Assert.Equal(["postgres", "-c", "max_connections=200"], draft.ToSettings().Command!);
        Assert.Contains(SettingField.Command, draft.ChangedFields());

        draft.Command = "";
        Assert.Null(draft.ToSettings().Command);
    }

    [Fact]
    public void Shell_lines_split_and_join_back()
    {
        Assert.Equal(["a", "b c", "d\"e", "", "f\\g"], ShellLine.Split("a \"b c\" 'd\"e' \"\" f\\\\g"));
        var words = new[] { "sh", "-c", "echo \"hi\" && exit 0" };
        Assert.Equal(words, ShellLine.Split(ShellLine.Join(words)));
    }

    [Fact]
    public void A_custom_health_check_is_a_shell_command()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.HealthMode = HealthMode.Custom;
        draft.HealthCommand = "pg_isready -U postgres";
        draft.HealthIntervalSeconds = 10;
        draft.HealthRetries = 5;
        var health = draft.ToSettings().Health;
        Assert.Equal(HealthMode.Custom, health.Mode);
        Assert.Equal(["CMD-SHELL", "pg_isready -U postgres"], health.Test!);
        Assert.Equal(TimeSpan.FromSeconds(10), health.Interval);
        Assert.Equal(5, health.Retries);
        Assert.Contains(SettingField.Health, draft.ChangedFields());

        Assert.Equal("pg_isready -U postgres", ContainerSettingsDraft.HealthText(["CMD-SHELL", "pg_isready -U postgres"]));
        Assert.Equal("curl -f http://localhost/", ContainerSettingsDraft.HealthText(["CMD", "curl", "-f", "http://localhost/"]));
    }

    [Fact]
    public void Gitberths_and_composes_labels_are_read_only()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        Assert.True(draft.Labels.Single(l => l.Key == "gitberth.app").ReadOnly);
        Assert.False(draft.Labels.Single(l => l.Key == "owner").ReadOnly);
        Assert.True(ContainerSettingsDraft.IsReservedLabel("com.docker.compose.project"));

        draft.Labels.Single(l => l.Key == "owner").Value = "you";
        var labels = draft.ToSettings().Labels;
        Assert.Equal("you", labels["owner"]);
        Assert.Equal("postgres", labels["gitberth.app"]);
        Assert.Equal([SettingField.Labels], draft.ChangedFields());
    }

    [Fact]
    public void Volumes_rows_change_add_and_go()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        var added = draft.AddMount(MountKind.Bind);
        Assert.Empty(draft.ChangedFields());   // nothing in it yet
        added.Source = "/srv/backups";
        added.Target = "/backups";
        added.ReadOnly = true;
        Assert.Equal(new MountSetting(MountKind.Bind, "/srv/backups", "/backups", true), draft.ToSettings().Mounts[1]);
        Assert.Same(draft.Loaded.Mounts[0], draft.ToSettings().Mounts[0]);

        draft.RemoveMount(draft.Mounts[0]);
        Assert.Equal(["/backups"], draft.ToSettings().Mounts.Select(m => m.Target));
    }

    [Fact]
    public void Advanced_text_fields_read_back_as_lists_and_maps()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        draft.ExtraHosts = "printer.lan:10.1.1.20\n\n nas:10.1.1.5 ";
        draft.Dns = "1.1.1.1, 9.9.9.9";
        draft.Sysctls = "net.core.somaxconn=1024";
        draft.Ulimits = "nofile=65536:65536\nnproc=512";
        draft.Devices = "/dev/dri:/dev/dri\n/dev/ttyUSB0";
        var s = draft.ToSettings();
        Assert.Equal(["printer.lan:10.1.1.20", "nas:10.1.1.5"], s.ExtraHosts);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], s.Dns);
        Assert.Equal("1024", s.Sysctls["net.core.somaxconn"]);
        Assert.Equal([new UlimitSetting("nofile", 65536, 65536), new UlimitSetting("nproc", 512, 512)], s.Ulimits);
        Assert.Equal([new DeviceSetting("/dev/dri", "/dev/dri"), new DeviceSetting("/dev/ttyUSB0", "/dev/ttyUSB0")], s.Devices);
        Assert.Equal(
            [SettingField.ExtraHosts, SettingField.Dns, SettingField.Ulimits, SettingField.Devices, SettingField.Sysctls],
            draft.ChangedFields().Order());
    }

    [Fact]
    public void The_edit_holds_loaded_and_wanted_for_the_plan()
    {
        var loaded = Postgres();
        var draft = ContainerSettingsDraft.From(loaded);
        var options = AppOptionsDraft.From(new AppOptions { Name = "postgres", GatherStats = true });
        draft.Env[0].Value = "rotated";
        options.Pinned = true;
        var edit = new SettingsEdit("abc", draft.Loaded, draft.ToSettings(), options.Loaded, options.ToOptions());
        Assert.Same(loaded, edit.Loaded);
        Assert.Equal("rotated", edit.Wanted.Env[0].Value);
        Assert.True(edit.WantedOptions.Pinned);
        Assert.False(edit.LoadedOptions.Pinned);
    }

    [Fact]
    public void Gitberths_own_settings_say_what_changed()
    {
        var options = AppOptionsDraft.From(new AppOptions { Name = "postgres", HealthCheckPath = null, ScheduledRestart = null });
        Assert.False(options.Dirty);
        options.Name = "  ";
        Assert.Equal("An app needs a name.", options.NameProblem);
        Assert.Equal("postgres", options.ToOptions().Name);
        options.Name = "Postgres";
        options.HealthCheckPath = "/health";
        options.RestartOn = true;
        options.RestartAmount = 6;
        options.RestartUnit = RestartUnit.Hours;
        Assert.Equal(["name", "health URL", "scheduled restart"], options.Changed());
        Assert.Equal(new RestartInterval(6, RestartUnit.Hours), options.ToOptions().ScheduledRestart);

        // An empty health URL is none.
        options.HealthCheckPath = "";
        Assert.Null(options.ToOptions().HealthCheckPath);
    }

    [Fact]
    public void A_git_apps_branch_and_build_need_a_build_and_auto_deploy_doesnt()
    {
        var git = GitOptionsDraft.From(new GitOptions("main", null, DockerfileMode.Auto, null, null, true, true));
        Assert.False(git.Dirty);
        git.AutoDeploy = false;
        Assert.True(git.Dirty);
        Assert.False(git.NeedsBuild);
        git.Subdirectory = "/website/";
        Assert.Equal("website", git.ToOptions().Subdirectory);
        Assert.True(git.NeedsBuild);
    }

    // SEC-2: the Environment tab's per-row Secret switch.

    [Fact]
    public void SEC2_switching_a_variable_secret_is_a_change_saved_in_its_setting_and_undone()
    {
        var loaded = Postgres() with { Env = [.. Postgres().Env, new("LOG_LEVEL", "info")] };
        var draft = ContainerSettingsDraft.From(loaded);
        var row = draft.Env.Single(r => r.Key == "LOG_LEVEL");
        Assert.False(row.IsSecret);

        row.Secret = true;
        Assert.True(row.Changed);
        Assert.Equal([SettingField.Env], draft.ChangedFields());
        Assert.True(draft.ToSettings().Env.Single(e => e.Key == "LOG_LEVEL").Secret);

        row.Undo();
        Assert.False(row.Secret);
        Assert.False(draft.Dirty);
    }

    [Fact]
    public void SEC2_a_new_variable_holding_a_url_password_is_always_secret_and_saved_so()
    {
        var draft = ContainerSettingsDraft.From(Postgres());
        var row = draft.AddEnv("DATABASE_URL", "postgres://app:hunter2@postgres:5432/app");
        Assert.False(row.Secret);
        Assert.True(row.AlwaysSecret);
        Assert.True(row.IsSecret);
        Assert.True(draft.ToSettings().Env.Single(e => e.Key == "DATABASE_URL").Secret);
        Assert.Equal(["DATABASE_URL"], draft.SecretKeys([]));

        // A password-like name is secret on its own too, whatever its value.
        Assert.True(draft.AddEnv("API_TOKEN", "abc").AlwaysSecret);
    }

    [Fact]
    public void SEC2_the_apps_secret_names_follow_only_the_rows_the_owner_touched()
    {
        var loaded = Postgres() with { Env = [.. Postgres().Env, new("LOG_LEVEL", "info"), new("REGION", "eu")] };
        // REGION is marked secret by the app; the container itself can't say so.
        var draft = ContainerSettingsDraft.From(loaded, ["REGION"]);
        Assert.True(draft.Env.Single(r => r.Key == "REGION").Secret);
        Assert.False(draft.Dirty);
        // Untouched rows keep the app's list as it is, even POSTGRES_PASSWORD, which the list doesn't name.
        Assert.Equal(["REGION"], draft.SecretKeys(["REGION"]));

        draft.Env.Single(r => r.Key == "LOG_LEVEL").Secret = true;
        draft.Env.Single(r => r.Key == "REGION").Secret = false;
        Assert.Equal(["LOG_LEVEL"], draft.SecretKeys(["REGION"]));
        Assert.True(draft.Dirty);
    }
}
