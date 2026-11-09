using System.Text.Json.Nodes;
using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// A container recreated on a new image takes the new image's defaults (section 2.6): what the old image supplied comes
/// out of the body, and what the owner set stays. postgres 18.0 to 18.1 is the case it exists for.
/// </summary>
public sealed class ImageRebaseTests
{
    private static JsonObject Postgres(string version, string path, JsonObject? extra = null)
    {
        var config = new JsonObject
        {
            ["Env"] = new JsonArray($"PATH={path}", "GOSU_VERSION=1.19", "LANG=en_US.utf8", "PG_MAJOR=18", $"PG_VERSION={version}", "PGDATA=/var/lib/postgresql/18/docker"),
            ["Entrypoint"] = new JsonArray("docker-entrypoint.sh"),
            ["Cmd"] = new JsonArray("postgres"),
            ["ExposedPorts"] = new JsonObject { ["5432/tcp"] = new JsonObject() },
            ["Volumes"] = new JsonObject { ["/var/lib/postgresql"] = new JsonObject() },
            ["StopSignal"] = "SIGINT",
            ["Labels"] = new JsonObject { ["org.opencontainers.image.version"] = version },
        };
        foreach (var (key, value) in extra ?? []) config[key] = value?.DeepClone();
        return ContainerSettingsJsonTests.Image("sha256:" + version.Replace(".", "", StringComparison.Ordinal).PadRight(64, '0'), config);
    }

    private static readonly JsonObject Old = Postgres("18.0", "/usr/local/bin:/usr/lib/postgresql/18/bin");
    private static readonly JsonObject New = Postgres("18.1", "/usr/local/bin:/usr/bin:/usr/lib/postgresql/18/bin");

    // The body a clone of a container on the old image makes: the image's config, the container's on top.
    private static JsonObject Body(Action<JsonObject>? change = null)
    {
        var body = (JsonObject)Old["Config"]!.DeepClone();
        body["Env"] = new JsonArray("POSTGRES_PASSWORD=s3cret-pass", "PATH=/usr/local/bin:/usr/lib/postgresql/18/bin", "GOSU_VERSION=1.19", "LANG=en_US.utf8",
            "PG_MAJOR=18", "PG_VERSION=18.0", "PGDATA=/var/lib/postgresql/18/docker");
        body["Hostname"] = "0ebf31bbcd07";
        body["Image"] = "postgres:18";
        body["Labels"] = new JsonObject { ["org.opencontainers.image.version"] = "18.0", ["team"] = "data" };
        body["HostConfig"] = new JsonObject { ["PortBindings"] = new JsonObject() };
        change?.Invoke(body);
        return body;
    }

    [Fact]
    public void Takes_the_old_images_variables_out_and_keeps_the_containers_own()
    {
        var body = Body();

        var notes = ImageRebase.Apply(body, Old, New);

        // PG_VERSION and PATH follow the new image; POSTGRES_PASSWORD is the container's own.
        Assert.Equal("""["POSTGRES_PASSWORD=s3cret-pass"]""", body["Env"]!.ToJsonString());
        Assert.Equal(
        [
            "From the new image: PATH /usr/local/bin:/usr/lib/postgresql/18/bin → /usr/local/bin:/usr/bin:/usr/lib/postgresql/18/bin",
            "From the new image: PG_VERSION 18.0 → 18.1",
        ], notes);
    }

    [Fact]
    public void A_variable_set_to_another_value_stays()
    {
        var body = Body(b => b["Env"] = new JsonArray("POSTGRES_PASSWORD=s3cret-pass", "PGDATA=/data/pg", "PG_VERSION=18.0"));

        var notes = ImageRebase.Apply(body, Old, New);

        Assert.Equal("""["POSTGRES_PASSWORD=s3cret-pass","PGDATA=/data/pg"]""", body["Env"]!.ToJsonString());
        Assert.Contains("From the new image: PG_VERSION 18.0 → 18.1", notes);
        Assert.DoesNotContain(notes, n => n.Contains("PGDATA", StringComparison.Ordinal));
    }

    [Fact]
    public void Takes_out_the_old_images_entrypoint_and_command()
    {
        var body = Body();

        ImageRebase.Apply(body, Old, New);

        Assert.False(body.ContainsKey("Entrypoint"));
        Assert.False(body.ContainsKey("Cmd"));
    }

    [Fact]
    public void Keeps_a_command_of_the_containers_own_on_the_images_entrypoint()
    {
        var body = Body(b => b["Cmd"] = new JsonArray("postgres", "-c", "max_connections=200"));

        ImageRebase.Apply(body, Old, New);

        Assert.False(body.ContainsKey("Entrypoint"));
        Assert.Equal("""["postgres","-c","max_connections=200"]""", body["Cmd"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_an_entrypoint_of_its_own_and_its_command()
    {
        var body = Body(b => b["Entrypoint"] = new JsonArray("/usr/local/bin/wrapper.sh"));

        ImageRebase.Apply(body, Old, New);

        Assert.Equal("""["/usr/local/bin/wrapper.sh"]""", body["Entrypoint"]!.ToJsonString());
        Assert.Equal("""["postgres"]""", body["Cmd"]!.ToJsonString());
    }

    [Fact]
    public void Takes_out_the_old_images_labels_health_check_stop_signal_ports_and_volumes()
    {
        var check = JsonNode.Parse("""{"Test":["CMD-SHELL","pg_isready"],"Interval":5000000000}""")!;
        var old = Postgres("18.0", "/usr/local/bin:/usr/lib/postgresql/18/bin", new JsonObject { ["Healthcheck"] = check.DeepClone(), ["User"] = "postgres", ["WorkingDir"] = "/" });
        var body = Body(b =>
        {
            b["Healthcheck"] = check.DeepClone();
            b["User"] = "postgres";
            b["WorkingDir"] = "/srv";
            b["ExposedPorts"] = new JsonObject { ["5432/tcp"] = new JsonObject(), ["9187/tcp"] = new JsonObject() };
        });

        ImageRebase.Apply(body, old, New);

        Assert.Equal("""{"team":"data"}""", body["Labels"]!.ToJsonString());
        Assert.False(body.ContainsKey("Healthcheck"));
        Assert.False(body.ContainsKey("StopSignal"));
        Assert.False(body.ContainsKey("User"));
        Assert.False(body.ContainsKey("Volumes"));
        // A working folder of its own stays; so does a port only the container exposes.
        Assert.Equal("/srv", (string)body["WorkingDir"]!);
        Assert.Equal("""{"9187/tcp":{}}""", body["ExposedPorts"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_a_health_check_of_its_own_a_published_port_and_the_hostname()
    {
        var body = Body(b =>
        {
            b["Healthcheck"] = JsonNode.Parse("""{"Test":["CMD-SHELL","pg_isready -U postgres"],"Interval":10000000000,"Retries":5}""");
            b["HostConfig"] = new JsonObject { ["PortBindings"] = JsonNode.Parse("""{"5432/tcp":[{"HostIp":"","HostPort":"5432"}]}""") };
        });

        ImageRebase.Apply(body, Old, New);

        Assert.True(body.ContainsKey("Healthcheck"));
        Assert.Equal("""{"5432/tcp":{}}""", body["ExposedPorts"]!.ToJsonString());
        // RabbitMQ and others name their data after it: the hostname always stays.
        Assert.Equal("0ebf31bbcd07", (string)body["Hostname"]!);
        Assert.Equal("postgres:18", (string)body["Image"]!);
    }
}
