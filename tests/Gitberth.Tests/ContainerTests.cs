using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>Every container on the host, whoever started it: what it is to Gitberth, how it's shown, and what to warn about.</summary>
public class ContainerTests
{
    private static readonly Dictionary<string, NavApp> Apps = new(StringComparer.Ordinal) { ["kanban"] = new NavApp(5, "kanban", "kanban") };

    private static ContainerSummary C(string name, string image, string state = "running", string status = "Up 3 hours",
        Dictionary<string, string>? labels = null, string command = "", string id = "") =>
        new(name, state, status, image, "", labels ?? [], command, id);

    [Fact]
    public void Tells_what_each_container_is_to_gitberth()
    {
        var app = new Dictionary<string, string> { ["gitberth.managed"] = "true", ["gitberth.app"] = "kanban" };
        Assert.Equal(ContainerRole.App, ContainerService.RoleOf(C("gb-kanban", "gitberth/kanban:1", labels: app), Apps, null));
        // A deploy's temporary container, and the one kept aside, carry the app's label too.
        Assert.Equal(ContainerRole.AppInternal, ContainerService.RoleOf(C("gb-kanban_next", "gitberth/kanban:2", labels: app), Apps, null));
        // Gitberth's container is named after its short ID unless the compose file says otherwise.
        Assert.Equal(ContainerRole.Self, ContainerService.RoleOf(C("gitberth", "gitberth:local", id: "3f2a1b9c8d7e0011aa"), Apps, "3f2a1b9c8d7e"));
        Assert.Equal(ContainerRole.Tunnel, ContainerService.RoleOf(C("cloudflare-tunnel", "cloudflare/cloudflared:latest"), Apps, null));
        Assert.Equal(ContainerRole.Database, ContainerService.RoleOf(C("postgres", "postgres:18"), Apps, null));
        Assert.Equal(ContainerRole.Database, ContainerService.RoleOf(C("cache", "ghcr.io/valkey-io/valkey:8@sha256:abc"), Apps, null));
        Assert.Equal(ContainerRole.Other, ContainerService.RoleOf(C("sample-web", "gitberth-sample-web"), Apps, null));
        // An app label for an app Gitberth no longer has is just another container.
        Assert.Equal(ContainerRole.Other, ContainerService.RoleOf(C("gb-old", "gitberth/old:1", labels: new() { ["gitberth.app"] = "old" }), Apps, null));
        // Gitberth's own helpers, which run its image for a moment, and a port forwarder an older Gitberth started.
        Assert.Equal(ContainerRole.Helper, ContainerService.RoleOf(C("gitberth-helper-ports-1a2b3c4d5e6f", "sha256:0c1a", labels: new() { ["gitberth.helper"] = "ports" }), Apps, null));
        Assert.Equal(ContainerRole.LegacyForwarder,
            ContainerService.RoleOf(C("gb-publish-postgres-5432", "alpine/socat:1.8.1.1", labels: new() { [ContainerService.PublishesLabel] = "postgres:5432" }), Apps, null));
    }

    [Theory]
    [InlineData("postgres:18", "postgres")]
    [InlineData("docker.io/library/postgres", "postgres")]
    [InlineData("registry.local:5000/team/redis:7-alpine", "redis")]
    [InlineData("postgres@sha256:0123", "postgres")]
    public void Reads_the_repository_of_an_image(string image, string repository) => Assert.Equal(repository, ContainerService.Repository(image));

    [Theory]
    [InlineData("running", "Up 3 hours", false, "Running")]
    [InlineData("running", "Up 3 hours (unhealthy)", true, "Unhealthy")]
    [InlineData("restarting", "Restarting (1) 5 seconds ago", true, "Restarting")]
    [InlineData("exited", "Exited (1) 3 minutes ago", true, "Exited (1)")]
    [InlineData("exited", "Exited (0) 3 minutes ago", false, "Stopped")]
    [InlineData("exited", "Exited (143) 3 minutes ago", false, "Stopped")]
    [InlineData("created", "Created", false, "Not started")]
    public void Says_which_containers_need_attention(string state, string status, bool attention, string pill)
    {
        var c = C("x", "x", state, status);
        Assert.Equal(attention, ContainerService.NeedsAttention(c));
        Assert.Equal(pill, ContainerService.Pill(c).Label);
    }

    [Theory]
    [InlineData("postgres://kanban:s3cret@postgres:5432/kanban", "postgres", true)]
    [InlineData("postgresql://kanban:s3cret@db.internal:5432/kanban", "postgres", false)]
    [InlineData("redis", "redis", true)]
    [InlineData("http://kanban:3000/api", "kanban", true)]
    [InlineData("Host=postgres;Port=5432;Database=app", "postgres", true)]
    [InlineData("my-postgres-backup", "postgres", false)]
    public void Spots_a_setting_that_names_another_container(string value, string host, bool names) =>
        Assert.Equal(names, ContainerService.NamesHost(value, host));

    [Fact]
    public void A_stopped_database_wont_start_next_to_a_running_one_on_the_same_volume()
    {
        ContainerMount Data(bool readOnly = false) => new("volume", "postgres-data", "/var/lib/docker/volumes/postgres-data/_data", "/var/lib/postgresql", readOnly);
        var old = new[] { Data() };
        Assert.Equal(
            "postgres is running with the same volume, postgres-data. Starting postgres-before-gitberth as well would put two database servers on the same files, which can damage them. Stop postgres first, or remove postgres-before-gitberth if it's an old copy.",
            ContainerService.SharedData("postgres-before-gitberth", old, [("postgres", "postgres:18", [Data()])]));
        // A backup that only reads it, a non-database, another volume, or nothing running: it starts.
        Assert.Null(ContainerService.SharedData("postgres-before-gitberth", old, [("pg-backup", "postgres:18", [Data(readOnly: true)])]));
        Assert.Null(ContainerService.SharedData("postgres-before-gitberth", old, [("files", "nginx:1.27", [Data()])]));
        Assert.Null(ContainerService.SharedData("postgres-before-gitberth", old, [("postgres", "postgres:18", [new ContainerMount("volume", "other-data", "", "/var/lib/postgresql", false)])]));
        Assert.Null(ContainerService.SharedData("postgres-before-gitberth", old, []));
    }

    [Fact]
    public void A_container_is_reached_by_its_name_and_aliases_but_not_its_short_id()
    {
        var d = Details(networks: [new ContainerNetwork("gitberth", ["postgres", "db", "3f2a1b9c8d7e"], "172.18.0.2")]);
        Assert.Equal(["postgres", "db"], ContainerService.HostNamesOf(d with { Name = "postgres" }));
    }

    [Theory]
    [InlineData("POSTGRES_PASSWORD", "hunter2", "••••")]
    [InlineData("DATABASE_URL", "postgres://kanban:hunter2@postgres:5432/kanban", "postgres://kanban:••••@postgres:5432/kanban")]
    [InlineData("PGDATA", "/var/lib/postgresql/data", "/var/lib/postgresql/data")]
    [InlineData("TUNNEL_TOKEN", "eyJhIjoi", "••••")]
    public void Hides_secret_values_until_asked(string key, string value, string shown) =>
        Assert.Equal(shown, ContainerService.MaskValue(key, value));

    [Fact]
    public void Hides_secrets_on_a_command_line()
    {
        Assert.Equal(["tunnel", "--no-autoupdate", "run", "--token", "••••"],
            ContainerService.MaskArgs(["tunnel", "--no-autoupdate", "run", "--token", "eyJhIjoiNzYx"]));
        Assert.Equal(["server", "--password=••••", "--port", "8080"], ContainerService.MaskArgs(["server", "--password=hunter2", "--port", "8080"]));
        Assert.Equal(["--db", "mysql://app:••••@db/app"], ContainerService.MaskArgs(["--db", "mysql://app:hunter2@db/app"]));
    }

    [Fact]
    public void Reads_a_container_as_docker_inspect_describes_it()
    {
        const string json = """
            [{
              "Id": "3f2a1b9c8d7e00", "Name": "/postgres", "Created": "2026-11-02T16:00:00.123456789Z", "Image": "sha256:abcdef0123456789",
              "RestartCount": 2,
              "State": { "Status": "running", "Running": true, "ExitCode": 0, "StartedAt": "2026-11-02T16:00:01Z", "FinishedAt": "0001-01-01T00:00:00Z",
                         "OOMKilled": false, "Health": { "Status": "healthy" } },
              "Config": { "Image": "postgres:18", "Env": ["POSTGRES_PASSWORD=hunter2", "PGDATA=/var/lib/postgresql/data", "EMPTY"],
                          "Cmd": ["postgres"], "Entrypoint": ["docker-entrypoint.sh"], "WorkingDir": "", "User": "", "Labels": { "team": "infra" } },
              "HostConfig": { "RestartPolicy": { "Name": "unless-stopped", "MaximumRetryCount": 0 }, "NetworkMode": "gitberth", "Memory": 1073741824, "NanoCpus": 1500000000,
                              "PortBindings": { "5432/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "5432" }] } },
              "Mounts": [{ "Type": "volume", "Name": "postgres-data", "Source": "/var/lib/docker/volumes/postgres-data/_data", "Destination": "/var/lib/postgresql/data", "RW": true },
                         { "Type": "bind", "Source": "/root/init", "Destination": "/docker-entrypoint-initdb.d", "RW": false }],
              "NetworkSettings": { "Networks": { "gitberth": { "Aliases": ["postgres", "3f2a1b9c8d7e"], "IPAddress": "172.18.0.2" } } }
            }]
            """;
        var d = Assert.Single(DockerCli.ParseContainerDetails(json));

        Assert.Equal(("postgres", "postgres:18", "running", true, 2, "healthy"), (d.Name, d.Image, d.Status, d.Running, d.RestartCount, d.Health));
        Assert.Null(d.FinishedAt);
        Assert.Equal(new DateTime(2026, 11, 2, 16, 0, 1, DateTimeKind.Utc), d.StartedAt);
        Assert.Equal(["POSTGRES_PASSWORD=hunter2", "PGDATA=/var/lib/postgresql/data", "EMPTY="], d.Env.Select(e => $"{e.Key}={e.Value}"));
        Assert.Equal(("unless-stopped", 1073741824L, 1.5), (d.RestartPolicy, d.MemoryLimit, d.Cpus));
        Assert.Equal(new PortBinding("127.0.0.1", 5432, 5432, "tcp"), Assert.Single(d.Ports));
        Assert.Equal([new ContainerMount("volume", "postgres-data", "postgres-data", "/var/lib/postgresql/data", false),
            new ContainerMount("bind", null, "/root/init", "/docker-entrypoint-initdb.d", true)], d.Mounts);
        Assert.Equal("infra", d.Labels["team"]);
        Assert.Equal(["postgres", "3f2a1b9c8d7e"], Assert.Single(d.Networks).Aliases);
    }

    [Fact]
    public void A_broken_inspect_answer_reads_as_nothing() => Assert.Empty(DockerCli.ParseContainerDetails("Error: No such object"));

    [Fact]
    public void Reads_the_ports_a_container_listens_on_published_or_not()
    {
        var d = Details([], exposed: [new ExposedPort(5432, "tcp"), new ExposedPort(9187, "tcp")], ports: [new PortBinding(null, 8080, 80, "tcp")]);
        Assert.Equal([new ExposedPort(80, "tcp"), new ExposedPort(5432, "tcp"), new ExposedPort(9187, "tcp")], ContainerService.ListeningPorts(d));
    }

    [Fact]
    public void Only_networks_with_names_count_for_reaching_it_by_name()
    {
        var d = Details([new ContainerNetwork("bridge", [], "172.17.0.2"), new ContainerNetwork("gitberth", ["postgres"], "172.20.0.5")]);
        Assert.Equal(["gitberth"], ContainerService.NamedNetworks(d).Select(n => n.Name));
    }

    [Fact]
    public void Each_old_forwarder_is_named_once_at_startup()
    {
        var forwarder = C("gb-publish-postgres-5432", "alpine/socat:1.8.1.1", labels: new() { [ContainerService.PublishesLabel] = "postgres:5432" });

        Assert.Equal("gb-publish-postgres-5432 is an old Gitberth port forwarder; remove it on postgres's Network tab.", StartupTasks.ForwarderWarning(forwarder));
        Assert.Null(StartupTasks.ForwarderWarning(C("postgres", "postgres:18")));
    }

    [Theory]
    [InlineData("postgres:18", true)]
    [InlineData("docker.io/library/redis:7-alpine", true)]
    [InlineData("valkey/valkey@sha256:0123", true)]
    [InlineData("nginx:stable-alpine", false)]
    [InlineData("", false)]
    public void Knows_a_database_by_its_image(string image, bool database) => Assert.Equal(database, ContainerService.IsDatabaseImage(image));

    private static ContainerDetails Details(IReadOnlyList<ContainerNetwork> networks, string image = "img", IReadOnlyList<KeyValuePair<string, string>>? env = null,
        IReadOnlyList<string>? cmd = null, IReadOnlyList<ExposedPort>? exposed = null, IReadOnlyList<PortBinding>? ports = null) =>
        new("id", "x", image, "sha256:1", null, "running", true, 0, null, null, null, false, 0, [], cmd ?? [], null, null, env ?? [],
            new Dictionary<string, string>(), "no", ports ?? [], [], networks, "bridge", 0, 0, exposed ?? []);
}
