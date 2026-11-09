using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>The YAML a change to a Compose-managed container becomes: only the keys that change, and never a secret's value.</summary>
public sealed class ComposeSnippetTests
{
    private static ComposeService Media()
    {
        var inspect = DockerFixtures.Inspect("sample-index");
        var labels = inspect["Config"]!["Labels"]!.AsObject();
        labels["com.docker.compose.project"] = "media";
        labels["com.docker.compose.service"] = "web";
        labels["com.docker.compose.project.config_files"] = "/opt/media/compose.yml";
        labels["com.docker.compose.project.working_dir"] = "/opt/media";
        return ComposeService.Of(inspect)!;
    }

    [Fact]
    public void Writes_only_the_keys_of_the_fields_that_change()
    {
        var target = new ContainerSettings
        {
            Image = "nginx:stable-alpine",
            Restart = new RestartSetting("on-failure", 3),
            Resources = new ResourceSetting(MemoryBytes: 512L << 20, Cpus: 1.5),
            Mounts = [new MountSetting(MountKind.Volume, "site", "/usr/share/nginx/html", ReadOnly: true), new MountSetting(MountKind.Tmpfs, "", "/cache", TmpfsBytes: 1048576)],
            Health = new HealthSetting(HealthMode.Custom, ["CMD-SHELL", "wget -qO- localhost"], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), 3),
            Labels = new Dictionary<string, string> { ["team"] = "media", ["com.docker.compose.project"] = "media" },
            Ports = [new PortRule(80, "tcp", PortAccess.Lan, 9100, "0.0.0.0"), new PortRule(9113, "tcp", PortAccess.HostOnly, 9113), new PortRule(8080, "tcp", PortAccess.Internal)],
        };
        var fields = new HashSet<SettingField>
            { SettingField.RestartPolicy, SettingField.Memory, SettingField.Cpu, SettingField.Mounts, SettingField.Health, SettingField.Labels, SettingField.Ports };

        var yaml = ComposeSnippet.For(Media(), target, fields, []);

        Assert.Equal("""
            services:
              web:
                ports:
                  - "0.0.0.0:9100:80"
                  - "127.0.0.1:9113:9113"
                expose: ["8080/tcp"]
                volumes:
                  - "site:/usr/share/nginx/html:ro"
                tmpfs:
                  - "/cache:size=1048576"
                restart: "on-failure:3"
                mem_limit: 512m
                cpus: 1.5
                healthcheck:
                  test: [CMD-SHELL, "wget -qO- localhost"]
                  interval: 30s
                  timeout: 5s
                  retries: 3
                labels:
                  team: media
            """.ReplaceLineEndings("\n"), yaml);
    }

    [Fact]
    public void Writes_secrets_as_variables_of_the_projects_env_file()
    {
        var target = new ContainerSettings
        {
            Image = "postgres:18",
            Env =
            [
                new EnvSetting("POSTGRES_PASSWORD", "s3cret-pass"),
                new EnvSetting("DATABASE_URL", "postgres://app:hunter22@db/app"),
                new EnvSetting("PRICE", "$5"),
                new EnvSetting("PIN", "1234"),
                new EnvSetting("PATH", "/usr/bin", FromImage: true),
            ],
        };

        var yaml = ComposeSnippet.For(Media(), target, new HashSet<SettingField> { SettingField.Env }, ["PIN"]);

        Assert.DoesNotContain("s3cret-pass", yaml);
        Assert.DoesNotContain("hunter22", yaml);
        Assert.DoesNotContain("1234", yaml);
        Assert.DoesNotContain("PATH", yaml);
        Assert.Equal("""
            # Put POSTGRES_PASSWORD, DATABASE_URL and PIN in the project's .env (/opt/media/.env): the file reads them from there.
            services:
              web:
                environment:
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                  DATABASE_URL: ${DATABASE_URL}
                  PRICE: "$$5"
                  PIN: ${PIN}
            """.ReplaceLineEndings("\n"), yaml);
    }

    [Fact]
    public void A_port_change_of_a_compose_container_is_the_same_snippet_as_before()
    {
        // The port-only snippet (ComposeService.Snippet) is what the Ports card shows, as it did.
        var bindings = new[] { new NewBinding(80, "tcp", "", 9100), new NewBinding(53, "udp", "127.0.0.1", 5353) };
        Assert.Equal("services:\n  web:\n    ports:\n      - \"9100:80\"\n      - \"127.0.0.1:5353:53/udp\"", Media().Snippet(bindings));
        _ = new JsonObject();
    }
}
