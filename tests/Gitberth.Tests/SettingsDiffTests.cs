using Gitberth.Apps;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>What a settings change changes, field by field and by content, with every value a line shows masked.</summary>
public sealed class SettingsDiffTests
{
    private static readonly ContainerSettings Base = new()
    {
        Image = "postgres:18",
        Env =
        [
            new EnvSetting("POSTGRES_PASSWORD", "s3cret-pass"),
            new EnvSetting("DATABASE_URL", "postgres://app:hunter22@postgres/app"),
            new EnvSetting("DEBUG", "1"),
            new EnvSetting("PATH", "/usr/bin", FromImage: true),
        ],
        Resources = new ResourceSetting(MemoryBytes: 1L << 30),
        Restart = new RestartSetting("unless-stopped"),
    };

    [Fact]
    public void Compares_lists_and_maps_by_content()
    {
        // Equal content in new lists isn't a change; record equality would say it is.
        var copy = Base with { Env = Base.Env.ToList(), Labels = new Dictionary<string, string>() };
        Assert.Empty(SettingsDiff.Changed(Base, copy));
        // A LAN port's 0.0.0.0 or "" is the same port.
        Assert.Empty(SettingsDiff.Changed(Base with { Ports = [new PortRule(80, "tcp", PortAccess.Lan, 9100, "0.0.0.0")] },
            Base with { Ports = [new PortRule(80, "tcp", PortAccess.Lan, 9100)] }));
        Assert.Equal([SettingField.Env, SettingField.Memory], SettingsDiff.Changed(Base, Base with
        {
            Env = [.. Base.Env, new EnvSetting("TZ", "UTC")],
            Resources = Base.Resources with { MemoryBytes = 512L << 20 },
        }));
    }

    [Fact]
    public void A_memory_limit_changed_outside_gitberth_survives_an_environment_change()
    {
        // The page loaded at 1 GiB; meanwhile docker update set 2 GiB. The owner changes the environment only.
        var loaded = Base;
        var wanted = Base with { Env = [.. Base.Env.Where(e => e.Key != "DEBUG")] };
        var live = Base with { Resources = Base.Resources with { MemoryBytes = 2L << 30 } };

        var fields = SettingsDiff.Changed(loaded, wanted);
        var target = SettingsDiff.Merge(live, wanted, fields);

        Assert.Equal([SettingField.Env], fields);
        Assert.Equal(2L << 30, target.Resources.MemoryBytes);
        Assert.DoesNotContain(target.Env, e => e.Key == "DEBUG");
    }

    [Fact]
    public void Masks_a_secret_key_a_url_password_and_the_owners_markings()
    {
        var wanted = Base with
        {
            Env =
            [
                new EnvSetting("POSTGRES_PASSWORD", "n3w-pass"),
                new EnvSetting("DATABASE_URL", "postgres://app:hunter23@postgres/app"),
                new EnvSetting("PATH", "/usr/bin", FromImage: true),
                new EnvSetting("REDIS_URL", "redis://redis:6379"),
                new EnvSetting("PIN", "4321"),
            ],
        };

        var lines = SettingsDiff.Lines(Base, wanted, SettingsDiff.Changed(Base, wanted), ["PIN"], _ => ApplyKind.Recreate);

        Assert.Equal(
        [
            new SettingLine(SettingField.Env, "POSTGRES_PASSWORD", "••••", "••••", ApplyKind.Recreate),
            new SettingLine(SettingField.Env, "DATABASE_URL", "postgres://app:••••@postgres/app", "postgres://app:••••@postgres/app", ApplyKind.Recreate),
            new SettingLine(SettingField.Env, "Added REDIS_URL", "", "redis://redis:6379", ApplyKind.Recreate),
            new SettingLine(SettingField.Env, "Added PIN", "", "••••", ApplyKind.Recreate),
            new SettingLine(SettingField.Env, "Removed DEBUG", "1", "", ApplyKind.Recreate),
        ], lines);
        Assert.All(lines, l => Assert.DoesNotContain("hunter", l.Before + l.After));
    }

    [Fact]
    public void Masks_a_token_option_and_a_token_argument_in_the_command()
    {
        var before = new ContainerSettings { Image = "cloudflare/cloudflared:latest", Command = ["tunnel", "--no-autoupdate", "run", "--token", "eyJhIjoiYmMifQ"] };
        var after = before with { Command = ["tunnel", "run", "--token=eyJhIjoiZGQifQ", "TUNNEL_TOKEN=eyJhIjoiZWUifQ"] };

        var line = Assert.Single(SettingsDiff.Lines(before, after, new HashSet<SettingField> { SettingField.Command }, [], _ => ApplyKind.Recreate));

        Assert.Equal("tunnel --no-autoupdate run --token ••••", line.Before);
        Assert.Equal("tunnel run --token=•••• TUNNEL_TOKEN=••••", line.After);
    }

    [Fact]
    public void Collapses_a_long_list()
    {
        var wanted = Base with { Env = [.. Base.Env, .. Enumerable.Range(1, 7).Select(i => new EnvSetting($"FEATURE_{i}", "on"))] };

        var lines = SettingsDiff.Lines(Base, wanted, new HashSet<SettingField> { SettingField.Env }, [], _ => ApplyKind.Recreate);

        Assert.Equal(6, lines.Count);
        Assert.Equal("and 2 more variables", lines[^1].Label);
        var hosts = Base with { ExtraHosts = [.. Enumerable.Range(1, 8).Select(i => $"h{i}:10.1.1.{i}")] };
        Assert.Equal("h1:10.1.1.1, h2:10.1.1.2, h3:10.1.1.3, h4:10.1.1.4, h5:10.1.1.5 and 3 more",
            Assert.Single(SettingsDiff.Lines(Base, hosts, new HashSet<SettingField> { SettingField.ExtraHosts }, [], _ => ApplyKind.Recreate)).After);
    }

    [Fact]
    public void A_summary_names_what_changed_and_never_a_value()
    {
        var wanted = Base with
        {
            Env = [.. Base.Env.Select(e => e.Key == "DATABASE_URL" ? e with { Value = "postgres://app:hunter23@db/app" } : e)],
            Resources = Base.Resources with { MemoryBytes = 512L << 20 },
        };

        var summary = SettingsDiff.Summary(Base, wanted, SettingsDiff.Changed(Base, wanted));

        Assert.Equal("changed DATABASE_URL and the memory limit", summary);
        Assert.DoesNotContain("hunter", summary);
        var many = Base with { Env = [], Image = "postgres:19", Restart = new RestartSetting("always") };
        Assert.Equal("6 setting changes", SettingsDiff.Summary(Base, many, SettingsDiff.Changed(Base, many)));
    }

    [Fact]
    public void Live_lines_say_how_they_apply()
    {
        var wanted = Base with { Restart = new RestartSetting("always"), Resources = Base.Resources with { MemoryBytes = 512L << 20, Cpus = 1.5 } };

        var lines = SettingsDiff.Lines(Base, wanted, SettingsDiff.Changed(Base, wanted), [], f => SettingFields.KindOf(f));

        Assert.Equal(
        [
            new SettingLine(SettingField.RestartPolicy, "Restart policy", "Unless stopped", "Always", ApplyKind.Live),
            new SettingLine(SettingField.Memory, "Memory limit", "1 GiB", "512 MiB", ApplyKind.Live),
            new SettingLine(SettingField.Cpu, "CPU limit", "none", "1.5 CPUs", ApplyKind.Live),
        ], lines);
    }
}
