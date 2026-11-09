using System.Text.Json.Nodes;
using Gitberth.Components.Shared.Network;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// What a standalone container's page works out before anything changes: the rows of its Ports card (from its image, its
/// bindings and Gitberth's notes), what the bar says applying them does, the Compose change for a container Compose owns,
/// each port change's title and button, the banners after a restart of Gitberth, "Last change", and where Connect and the
/// Addresses card's URL rows point. postgres is the shared database: postgres:18, port 5432, unpublished, on gitberth.
/// </summary>
public sealed class ContainerPageTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Db _db;

    public ContainerPageTests()
    {
        _connection.Open();
        _db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using var context = _db.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // A container's inspect with only what its ports are read from.
    private static JsonObject Inspect(string exposed, string bindings, bool running = true, string live = "{}", string id = "0ebf31bbcd07", bool publishAll = false) =>
        JsonNode.Parse($$"""
            {
              "Id": "{{id}}{{new string('a', 52)}}",
              "Name": "/postgres",
              "State": { "Running": {{(running ? "true" : "false")}} },
              "Config": { "Image": "postgres:18", "ExposedPorts": {{exposed}} },
              "HostConfig": { "PortBindings": {{bindings}}, "PublishAllPorts": {{(publishAll ? "true" : "false")}} },
              "NetworkSettings": { "Ports": {{live}} }
            }
            """)!.AsObject();

    private static JsonObject Postgres(bool running = true) => Inspect("""{ "5432/tcp": {} }""", "{}", running);

    private static ContainerPortNote Note(int port, string protocol = "tcp") => new() { ContainerName = "postgres", Port = port, Protocol = protocol };

    private static ContainerPortRow Row(ContainerPortsDraft draft, int port, string protocol = "tcp") =>
        draft.Rows.First(r => r.ContainerPort == port && r.Protocol == protocol);

    [Fact]
    public void Rows_come_from_the_image_the_bindings_and_the_notes()
    {
        var inspect = Inspect(
            """{ "5432/tcp": {}, "8080/tcp": {}, "9000/tcp": {} }""",
            """{ "8080/tcp": [{ "HostIp": "", "HostPort": "9101" }], "9000/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "9000" }], "53/udp": [{ "HostIp": "0.0.0.0", "HostPort": "53" }] }""");
        // A note for a port it declares anyway shows once, as the container has it.
        var draft = ContainerPortsDraft.From(inspect, ["5432/tcp"], [Note(8081), Note(5432)]);

        Assert.Equal(["53/udp", "5432/tcp", "8080/tcp", "9000/tcp", "8081/tcp"], draft.Rows.Select(r => r.Key));
        Assert.Equal((PortOrigin.Exposed, PortAccess.Internal, (int?)null), (Row(draft, 5432).Origin, Row(draft, 5432).Access, Row(draft, 5432).HostPort));
        Assert.Equal((PortOrigin.Bound, PortAccess.Lan, (int?)9101), (Row(draft, 8080).Origin, Row(draft, 8080).Access, Row(draft, 8080).HostPort));
        Assert.Equal((PortAccess.HostOnly, (int?)9000), (Row(draft, 9000).Access, Row(draft, 9000).HostPort));
        // 0.0.0.0 is the LAN, and it stays 0.0.0.0, so an untouched row is published exactly as before.
        Assert.Equal((PortAccess.Lan, "0.0.0.0"), (Row(draft, 53, "udp").Access, Row(draft, 53, "udp").HostIp));
        Assert.Equal((PortOrigin.Note, PortAccess.Internal), (Row(draft, 8081).Origin, Row(draft, 8081).Access));
        // The image's own ports are only a hint; the container's own rows keep their port, notes and added rows can go.
        Assert.Equal(["5432/tcp"], draft.ImageExposed);
        Assert.True(Row(draft, 8080).Fixed);
        Assert.False(Row(draft, 8080).Removable);
        Assert.True(Row(draft, 8081).Removable);
        Assert.Equal((false, 0), (draft.Dirty, draft.Changes));
    }

    [Fact]
    public void A_port_docker_picked_at_random_can_be_kept_or_moved()
    {
        var inspect = Inspect("""{ "8080/tcp": {} }""", """{ "8080/tcp": [{ "HostIp": "", "HostPort": "" }] }""",
            live: """{ "8080/tcp": [{ "HostIp": "0.0.0.0", "HostPort": "32768" }, { "HostIp": "::", "HostPort": "32768" }] }""");
        var draft = ContainerPortsDraft.From(inspect, [], []);
        var row = Row(draft, 8080);
        Assert.Equal((true, (int?)32768, (int?)32768), (row.Random, row.Picked, row.HostPort));
        Assert.False(draft.Dirty);

        // [Keep 32768]: the same port, fixed from now on.
        row.Random = false;
        Assert.Equal((true, true, 1), (draft.Dirty, draft.NeedsRecreate, draft.Changes));
        Assert.Equal(new PortRule(8080, "tcp", PortAccess.Lan, 32768, ""), Assert.Single(draft.Rules()));

        // Stopped, Docker hasn't picked one: the row has no host port until one is chosen.
        var stopped = ContainerPortsDraft.From(Inspect("""{ "8080/tcp": {} }""", "{}", running: false, publishAll: true), [], []);
        Assert.Equal((true, (int?)null, (int?)null), (Row(stopped, 8080).Random, Row(stopped, 8080).Picked, Row(stopped, 8080).HostPort));
    }

    [Fact]
    public void A_port_only_noted_restarts_nothing()
    {
        var draft = ContainerPortsDraft.From(Postgres(), ["5432/tcp"], []);
        draft.Rows.Add(new ContainerPortRow { ContainerPort = 8081, Access = PortAccess.Internal });

        Assert.Equal((true, false, 1), (draft.Dirty, draft.NeedsRecreate, draft.Changes));
        Assert.Equal([(8081, "tcp")], draft.Notes());
        Assert.Equal("1 change. Nothing restarts.", ContainerPortsBar.Line(draft.Changes, draft.NeedsRecreate, "postgres", running: true, database: true));

        // A row being added that has no port yet isn't a change.
        var started = ContainerPortsDraft.From(Postgres(), [], []);
        started.Rows.Add(new ContainerPortRow());
        Assert.Equal((false, 0), (started.Dirty, started.Changes));

        // A note taken away is a change too, and restarts nothing.
        var noted = ContainerPortsDraft.From(Postgres(), [], [Note(8081)]);
        noted.Rows.Remove(Row(noted, 8081));
        Assert.Equal((true, false, 1), (noted.Dirty, noted.NeedsRecreate, noted.Changes));
        Assert.Empty(noted.Notes());
    }

    [Fact]
    public void Publishing_a_port_is_a_change_that_recreates_it()
    {
        var draft = ContainerPortsDraft.From(Postgres(), ["5432/tcp"], []);
        var row = Row(draft, 5432);
        (row.Access, row.HostPort) = (PortAccess.Lan, 5432);

        Assert.Equal((true, true, 1), (draft.Dirty, draft.NeedsRecreate, draft.Changes));
        Assert.False(draft.Unchanged(row));
        Assert.Equal(new PortRule(5432, "tcp", PortAccess.Lan, 5432), Assert.Single(draft.Rules()));

        // An added port that's published is one too, and is kept as a note, so it's still listed if the change fails.
        draft.Rows.Add(new ContainerPortRow { ContainerPort = 8081, Access = PortAccess.Lan, HostPort = 8081 });
        Assert.Equal((2, 2), (draft.Changes, draft.Rules().Count));
        Assert.Equal([(8081, "tcp")], draft.Notes());

        // Back as it was: nothing to apply.
        draft.Rows.RemoveAt(draft.Rows.Count - 1);
        (row.Access, row.HostPort) = (PortAccess.Internal, null);
        Assert.Equal((false, false, 0), (draft.Dirty, draft.NeedsRecreate, draft.Changes));
        Assert.True(draft.Unchanged(row));
    }

    [Fact]
    public void The_bar_says_what_applying_does()
    {
        Assert.Equal("1 change. Applying recreates postgres: about 5 s offline.", ContainerPortsBar.Line(1, recreate: true, "postgres", running: true, database: true));
        Assert.Equal("3 changes. Applying recreates postgres once: about 5 s offline.", ContainerPortsBar.Line(3, true, "postgres", true, true));
        Assert.Equal("1 change. Applying recreates sample-web: about 3 s offline.", ContainerPortsBar.Line(1, true, "sample-web", true, database: false));
        Assert.Equal("1 change. postgres is stopped: it's recreated stopped, so nothing goes offline.", ContainerPortsBar.Line(1, true, "postgres", running: false, true));
        Assert.Equal("2 changes. Nothing restarts.", ContainerPortsBar.Line(2, recreate: false, "postgres", true, true));
        Assert.Equal("postgres changed since you started editing.", ContainerPortsBar.Stale("postgres"));
    }

    [Fact]
    public void Knows_when_the_container_changed_under_the_draft()
    {
        var draft = ContainerPortsDraft.From(Postgres(), [], [Note(8081)]);
        Assert.Equal(draft.Loaded, ContainerPortsDraft.KeyOf(Postgres(), [Note(8081)]));
        // Recreated (a new ID), published another way, restarted with a new random port, or noted elsewhere.
        Assert.NotEqual(draft.Loaded, ContainerPortsDraft.KeyOf(Inspect("""{ "5432/tcp": {} }""", "{}", id: "f00dbeefcafe"), [Note(8081)]));
        Assert.NotEqual(draft.Loaded, ContainerPortsDraft.KeyOf(Inspect("""{ "5432/tcp": {} }""", """{ "5432/tcp": [{ "HostIp": "", "HostPort": "5432" }] }"""), [Note(8081)]));
        Assert.NotEqual(draft.Loaded, ContainerPortsDraft.KeyOf(Postgres(), []));
    }

    [Fact]
    public void Says_what_is_wrong_with_the_rows_under_the_field()
    {
        var draft = ContainerPortsDraft.From(Postgres(), [], []);
        draft.Rows.Add(new ContainerPortRow { ContainerPort = 5432, Access = PortAccess.Internal });
        draft.Rows.Add(new ContainerPortRow { ContainerPort = 70000 });
        draft.Rows.Add(new ContainerPortRow { ContainerPort = 8081, Access = PortAccess.Lan });
        var problems = draft.Problems();

        Assert.Contains(new PortProblem(1, PortField.ContainerPort, "5432/tcp is listed twice."), problems);
        Assert.Contains(new PortProblem(2, PortField.ContainerPort, "Each port needs a container port between 1 and 65535."), problems);
        Assert.Contains(new PortProblem(3, PortField.HostPort, "Pick a host port between 1 and 65535."), problems);

        // Two published rows can't take one host port.
        var twice = ContainerPortsDraft.From(Postgres(), [], []);
        (Row(twice, 5432).Access, Row(twice, 5432).HostPort) = (PortAccess.Lan, 5432);
        twice.Rows.Add(new ContainerPortRow { ContainerPort = 5433, Access = PortAccess.Lan, HostPort = 5432 });
        Assert.Contains(new PortProblem(1, PortField.HostPort, "Host port 5432 is listed twice."), twice.Problems());
    }

    [Fact]
    public void Each_row_has_the_address_its_access_gives()
    {
        ContainerPortRow Port(int port, PortAccess access, int? host = null, string protocol = "tcp", string? ip = null) =>
            new() { ContainerPort = port, Access = access, HostPort = host, Protocol = protocol, HostIp = ip };

        Assert.Equal(("10.1.1.80:5432", (string?)null), ContainerPortAddress.Of(Port(5432, PortAccess.Lan, 5432), "10.1.1.80", "postgres", named: true, database: true));
        // A web port opens; a database's web console does too, its protocol port doesn't.
        Assert.Equal(("http://10.1.1.80:9101", "http://10.1.1.80:9101"), ContainerPortAddress.Of(Port(80, PortAccess.Lan, 9101), "10.1.1.80", "sample-index", true, false));
        Assert.Equal(("http://10.1.1.80:15672", "http://10.1.1.80:15672"), ContainerPortAddress.Of(Port(15672, PortAccess.Lan, 15672), "10.1.1.80", "rabbitmq", true, true));
        Assert.Equal(("10.1.1.80:9000", (string?)null), ContainerPortAddress.Of(Port(9000, PortAccess.Lan, 9000), "10.1.1.80", "minio", true, database: true));
        // The host only is never a link; a Custom row is on its own address; UDP never opens.
        Assert.Equal(("127.0.0.1:3002", (string?)null), ContainerPortAddress.Of(Port(80, PortAccess.HostOnly, 3002), "10.1.1.80", "web", true, false));
        Assert.Equal(("[fd00::5]:53", (string?)null), ContainerPortAddress.Of(Port(53, PortAccess.Custom, 53, "udp", "fd00::5"), "10.1.1.80", "dns", true, false));
        // Internal is by name, only on a named network: Docker's default bridge gives none.
        Assert.Equal(("postgres:5432", (string?)null), ContainerPortAddress.Of(Port(5432, PortAccess.Internal), "10.1.1.80", "postgres", named: true, true));
        Assert.Null(ContainerPortAddress.Of(Port(80, PortAccess.Internal), "10.1.1.80", "sample-index", named: false, false));
        Assert.Null(ContainerPortAddress.Of(Port(80, PortAccess.Lan), "10.1.1.80", "sample-index", true, false));
        Assert.Null(ContainerPortAddress.Of(new ContainerPortRow { HostPort = 8081 }, "10.1.1.80", "sample-index", true, false));
    }

    [Fact]
    public void Names_the_services_it_knows_by_their_port()
    {
        Assert.Equal("PostgreSQL", PortServices.Name(5432, "tcp"));
        Assert.Equal("RabbitMQ management", PortServices.Name(15672, "tcp"));
        Assert.Equal("AMQP", PortServices.Name(5672, "tcp"));
        Assert.Null(PortServices.Name(5432, "udp"));
        Assert.Null(PortServices.Name(8080, "tcp"));
    }

    [Fact]
    public void A_compose_container_gets_the_lines_for_its_file_and_the_command()
    {
        var compose = new ComposeService("db", "postgres", ["/opt/db/docker-compose.yml"], "/opt/db");
        var draft = ContainerPortsDraft.From(Postgres(), [], []);
        (Row(draft, 5432).Access, Row(draft, 5432).HostPort) = (PortAccess.Lan, 5432);
        var bindings = CloneSpec.Bindings(Postgres(), draft.Rules(), "");

        Assert.Equal("services:\n  postgres:\n    ports:\n      - \"5432:5432\"", compose.Snippet(bindings));
        Assert.Equal("In /opt/db/docker-compose.yml:", ComposeText.FileLine(compose));
        // The project and file come from its labels, so the command picks the project the stack was started as.
        Assert.Equal("cd /opt/db && docker compose -p db -f /opt/db/docker-compose.yml up -d postgres", ComposeText.CommandLine(compose));

        // Host only, and UDP.
        Row(draft, 5432).Access = PortAccess.HostOnly;
        Assert.Contains("- \"127.0.0.1:5432:5432\"", compose.Snippet(CloneSpec.Bindings(Postgres(), draft.Rules(), "")));
        Assert.Contains("- \"53:53/udp\"", compose.Snippet([new NewBinding(53, "udp", "", 53)]));

        // Without the labels that say where its file is.
        var unlabelled = new ComposeService("db", "postgres", [], null);
        Assert.Equal("In its Compose file:", ComposeText.FileLine(unlabelled));
        Assert.Equal("docker compose -p db up -d postgres", ComposeText.CommandLine(unlabelled));
        Assert.Null(ComposeText.CommandLine(new ComposeService("db", null, [], "/opt/db")));

        // Portainer keeps its stacks' files inside its own container: no path or command on the host reaches them.
        var portainer = new ComposeService("db", "postgres", ["/data/compose/12/docker-compose.yml"], "/data/compose/12");
        Assert.True(ComposeText.InPortainer(portainer));
        Assert.Equal("In its stack in Portainer:", ComposeText.FileLine(portainer));
        Assert.Null(ComposeText.CommandLine(portainer));
        Assert.False(ComposeText.InPortainer(compose));
    }

    [Fact]
    public void Each_way_into_a_port_change_has_its_title_and_button()
    {
        Assert.Equal(("Publish postgres on the LAN", "Recreate and publish"),
            (PortChangeText.Title(PortChangeKind.Publish, "postgres", 5432), PortChangeText.Button(PortChangeKind.Publish)));
        Assert.Equal(("Recreate postgres?", "Recreate"), (PortChangeText.Title(PortChangeKind.Apply, "postgres"), PortChangeText.Button(PortChangeKind.Apply)));
        Assert.Equal(("Change postgres's host port", "Recreate"),
            (PortChangeText.Title(PortChangeKind.ChangeHostPort, "postgres", 5432), PortChangeText.Button(PortChangeKind.ChangeHostPort)));
        Assert.Equal(("Unpublish 5432 on postgres?", "Recreate and unpublish"),
            (PortChangeText.Title(PortChangeKind.Unpublish, "postgres", 5432), PortChangeText.Button(PortChangeKind.Unpublish)));
        Assert.Equal("Recreating postgres", PortChangeText.Recreating("postgres"));
    }

    [Fact]
    public void Each_way_into_a_port_change_asks_for_the_ports_it_changes()
    {
        List<PortRule> current =
        [
            new(5432, "tcp", PortAccess.Internal),
            new(8080, "tcp", PortAccess.Lan, 9101, "0.0.0.0"),
            new(9000, "tcp", PortAccess.HostOnly, 9000, "127.0.0.1"),
        ];

        // Publish on LAN: the port goes on the LAN, whatever it had; the others stay as they are.
        Assert.Equal([new(8080, "tcp", PortAccess.Lan, 9101, "0.0.0.0"), new(9000, "tcp", PortAccess.HostOnly, 9000, "127.0.0.1"), new(5432, "tcp", PortAccess.Lan, 5432)],
            PortChanges.Publish(current, 5432, "tcp", 5432));
        Assert.Equal([new(5432, "tcp", PortAccess.Internal), new(8080, "tcp", PortAccess.Lan, 9101, "0.0.0.0"), new(9000, "tcp", PortAccess.Lan, 9001)],
            PortChanges.Publish(current, 9000, "tcp", 9001));

        // Change host port keeps the binding's address; Unpublish leaves the port Internal.
        Assert.Equal(new PortRule(8080, "tcp", PortAccess.Lan, 9201, "0.0.0.0"), PortChanges.ChangeHostPort(current, current[1], 9201)[1]);
        Assert.Equal([new(5432, "tcp", PortAccess.Internal), new(9000, "tcp", PortAccess.HostOnly, 9000, "127.0.0.1"), new(8080, "tcp", PortAccess.Internal)],
            PortChanges.Unpublish(current, current[1]));
        // A port with another binding left keeps that one.
        List<PortRule> twice = [new(8080, "tcp", PortAccess.Lan, 9101), new(8080, "tcp", PortAccess.HostOnly, 9102, "127.0.0.1")];
        Assert.Equal([twice[1]], PortChanges.Unpublish(twice, twice[0]));
    }

    [Fact]
    public void Publish_on_lan_offers_the_databases_own_port_first()
    {
        List<PortRule> current =
        [
            new(53, "udp", PortAccess.Internal),
            new(5432, "tcp", PortAccess.Internal),
            new(5672, "tcp", PortAccess.Internal),
            new(8080, "tcp", PortAccess.Lan, 8080),
            new(15672, "tcp", PortAccess.HostOnly, 15672, "127.0.0.1"),
        ];
        // Only the ports that aren't on the LAN yet; the database's own first, else the lowest TCP one.
        Assert.Equal(["5432/tcp", "5672/tcp", "15672/tcp", "53/udp"], PortChanges.Candidates(current, databasePort: 5432).Select(r => r.Key));
        Assert.Equal(["5432/tcp", "5672/tcp", "15672/tcp", "53/udp"], PortChanges.Candidates(current, databasePort: null).Select(r => r.Key));
        Assert.Equal(["5672/tcp", "5432/tcp", "15672/tcp", "53/udp"], PortChanges.Candidates(current, databasePort: 5672).Select(r => r.Key));
        Assert.Empty(PortChanges.Candidates([new(8080, "tcp", PortAccess.Lan, 8080)], null));
    }

    [Fact]
    public void Progress_says_which_step_runs()
    {
        Assert.Equal("Recreating postgres: step 4 of 9…", PortChangeText.Progress("postgres", new PortChangeStep(4, 9, "Stopping postgres", StepState.Running)));
        Assert.Equal("Recreating postgres…", PortChangeText.Progress("postgres", null));
    }

    private static ContainerChange Change(ChangeOutcome outcome, string? notice, ChangeStep step = ChangeStep.Finished) => new()
    {
        ContainerName = "postgres",
        Summary = "published 5432/tcp on the LAN",
        OldId = "0ebf31bbcd07" + new string('a', 52),
        OldName = "postgres_old_0ebf31bbcd07",
        TempName = "postgres_new_0ebf31bbcd07",
        OldRestartPolicy = """{"Name":"unless-stopped"}""",
        Step = step,
        Outcome = outcome,
        Notice = notice,
        StartedAt = new DateTime(2026, 11, 4, 21, 14, 3, DateTimeKind.Utc),
    };

    [Fact]
    public void The_banner_after_a_restart_of_gitberth_says_how_the_change_ended()
    {
        const string finished = "Gitberth restarted while it recreated postgres. It finished the change: the new container runs and passed its checks.";
        Assert.Equal(new ChangeBanner(finished, Warning: false, Decide: false), ChangeBanner.Of(Change(ChangeOutcome.Done, finished)));
        const string putBack = "Gitberth restarted while it recreated postgres. It put the original back.";
        Assert.Equal(new ChangeBanner(putBack, Warning: true, Decide: false), ChangeBanner.Of(Change(ChangeOutcome.RolledBack, putBack)));
        const string interrupted = "A port change on postgres was interrupted. Both containers are kept: postgres (new, stopped) and postgres_old_0ebf31bbcd07 (the original).";
        Assert.Equal(new ChangeBanner(interrupted, Warning: true, Decide: true), ChangeBanner.Of(Change(ChangeOutcome.NeedsDecision, interrupted)));

        // A decision that waits is never dismissed: without its notice, its error says why.
        var waits = Change(ChangeOutcome.NeedsDecision, null) with { Error = "The new postgres is gone; the original is kept as postgres_old_0ebf31bbcd07." };
        Assert.Equal(new ChangeBanner(waits.Error!, true, true), ChangeBanner.Of(waits));
        // Dismissed, or a change that ended while the page was open: no banner.
        Assert.Null(ChangeBanner.Of(Change(ChangeOutcome.Done, null)));
        Assert.Null(ChangeBanner.Of(null));
    }

    [Fact]
    public void Last_change_says_when_what_and_how_long_it_was_offline()
    {
        Assert.Equal("Last change: none.", PortChangeText.LastChange(null, TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · offline 2.1 s.",
            PortChangeText.LastChange(Change(ChangeOutcome.Done, null) with { OfflineMs = 2100 }, TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN.", PortChangeText.LastChange(Change(ChangeOutcome.Done, null), TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · rolled back.",
            PortChangeText.LastChange(Change(ChangeOutcome.RolledBack, null), TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · nothing changed.",
            PortChangeText.LastChange(Change(ChangeOutcome.NothingChanged, null), TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · waits for your decision.",
            PortChangeText.LastChange(Change(ChangeOutcome.NeedsDecision, null), TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · waits for its first start.",
            PortChangeText.LastChange(Change(ChangeOutcome.Running, null, ChangeStep.WaitingForStart), TimeZoneInfo.Utc));
        Assert.Equal("Last change: 4 Nov 21:14 · published 5432/tcp on the LAN · under way.",
            PortChangeText.LastChange(Change(ChangeOutcome.Running, null, ChangeStep.Stopping), TimeZoneInfo.Utc));
    }

    private static ContainerDetails Database(string image = "postgres:18", params (string Key, string Value)[] env) =>
        new("0ebf31bbcd07", "postgres", image, "sha256:3a42", null, "running", true, 0, null, null, null, false, 0, [], ["postgres"], null, null,
            env.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToList(), new Dictionary<string, string>(), "unless-stopped", [], [],
            [new ContainerNetwork("gitberth", ["postgres"], "172.21.0.4")], "gitberth", 0, 0, [new ExposedPort(5432, "tcp")]);

    private static Address Lan(string text, int containerPort, int hostPort) => new(AddressKind.Lan, text, null, text, containerPort, "tcp", true, null, hostPort);

    private static Address Internal(string text, int containerPort) => new(AddressKind.Internal, text, null, text, containerPort, "tcp", true, null);

    private static Address OwnLan(string text, int containerPort) => new(AddressKind.OwnLan, text, null, text, containerPort, "tcp", true, null);

    [Fact]
    public void Connect_offers_the_hosts_address_then_its_own_and_the_name_inside()
    {
        List<Address> addresses = [Lan("10.1.1.80:5433", 5432, 5433), OwnLan("10.1.1.230:5432", 5432), Internal("postgres:5432", 5432)];

        Assert.Equal([new ConnectTarget("10.1.1.80", 5433, "the host"), new ConnectTarget("10.1.1.230", 5432, "its own address")], ConnectionRows.Lan(addresses, 5432));
        Assert.Equal(new ConnectTarget("postgres", 5432), ConnectionRows.Internal(addresses, 5432));
        Assert.Null(ConnectionRows.HostOnly(addresses, 5432));
        Assert.Equal(5432, ConnectionRows.DatabasePort(Database()));

        // Published on the host only: the LAN can't reach it.
        List<Address> hostOnly = [new(AddressKind.HostOnly, "127.0.0.1:5432", null, "127.0.0.1:5432", 5432, "tcp", true, null, 5432), Internal("postgres:5432", 5432)];
        Assert.Empty(ConnectionRows.Lan(hostOnly, 5432));
        Assert.Equal("127.0.0.1:5432", ConnectionRows.HostOnly(hostOnly, 5432));
        // Not on a named network: containers can't reach it by name.
        Assert.Null(ConnectionRows.Internal([Lan("10.1.1.80:5432", 5432, 5432)], 5432));
    }

    [Fact]
    public void The_url_rows_show_the_password_masked_and_copy_it()
    {
        var postgres = Database(env: [("POSTGRES_PASSWORD", "s3cret")]);
        List<Address> addresses = [Lan("10.1.1.80:5432", 5432, 5432), Internal("postgres:5432", 5432)];

        Assert.Equal(
        [
            new UrlRow("Postgres URL (LAN)", "postgres://postgres:••••@10.1.1.80:5432/postgres", "postgres://postgres:s3cret@10.1.1.80:5432/postgres"),
            new UrlRow("Postgres URL (internal)", "postgres://postgres:••••@postgres:5432/postgres", "postgres://postgres:s3cret@postgres:5432/postgres"),
        ], ConnectionRows.Urls(postgres, addresses));
        // Not published: only the internal one. Not a database Gitberth knows: none.
        Assert.Equal(["Postgres URL (internal)"], ConnectionRows.Urls(postgres, [Internal("postgres:5432", 5432)]).Select(u => u.Label));
        Assert.Empty(ConnectionRows.Urls(Database("nginx:stable-alpine"), addresses));
    }

    [Fact]
    public void Reads_the_host_out_of_an_address()
    {
        Assert.Equal("10.1.1.80", ConnectionRows.HostOf("http://10.1.1.80:8080"));
        Assert.Equal("10.1.1.80", ConnectionRows.HostOf("10.1.1.80:5432"));
        Assert.Equal("fd00::5", ConnectionRows.HostOf("[fd00::5]:53"));
        Assert.Equal("postgres", ConnectionRows.HostOf("postgres:5432"));
        Assert.Equal("10.1.1.230", ConnectionRows.HostOf("10.1.1.230"));
    }

    private static ContainerRow Forwarder(string name, string publishes) =>
        new(new ContainerSummary(name, "running", "Up 5 days", "alpine/socat:1.8.1.1", "0.0.0.0:5432->5432/tcp",
            new Dictionary<string, string> { [ContainerService.PublishesLabel] = publishes }), name, ContainerRole.Other, null, null);

    [Fact]
    public void Finds_an_old_forwarder_by_the_label_it_carries()
    {
        List<ContainerRow> rows =
        [
            Forwarder("gb-publish-postgres-5432", "postgres:5432"),
            Forwarder("gb-publish-postgres-old-5432", "postgres-old:5432"),
            Forwarder("gb-publish-broken", "postgres"),
        ];
        Assert.Equal([("gb-publish-postgres-5432", 5432)], LegacyForwarders.Of("postgres", rows));
        Assert.Empty(LegacyForwarders.Of("sample-index", rows));
    }

    [Fact]
    public void Gitberths_own_web_port_is_a_url_inside_docker_too()
    {
        List<Address> gitberth =
        [
            new(AddressKind.Lan, "http://10.1.1.80:8080", "http://10.1.1.80:8080", "http://10.1.1.80:8080", 8080, "tcp", true, null, 8080),
            Internal("gitberth:8080", 8080),
        ];
        Assert.Equal("http://gitberth:8080", ContainerAddressing.Shown(gitberth, ContainerRole.Self, [], "10.1.1.80")[1].Text);
        // Any other container's internal address stays as Docker's DNS has it.
        Assert.Equal("gitberth:8080", ContainerAddressing.Shown(gitberth, ContainerRole.Other, [], "10.1.1.80")[1].Text);
    }

    [Fact]
    public void A_noted_port_is_reached_by_name_like_the_containers_own()
    {
        List<Address> postgres = [Lan("10.1.1.80:5432", 5432, 5432), Internal("postgres:5432", 5432)];

        var shown = ContainerAddressing.Shown(postgres, ContainerRole.Database, [Note(5432), Note(9187)], "10.1.1.80", named: "postgres");
        Assert.Equal(["10.1.1.80:5432", "postgres:5432", "postgres:9187"], shown.Select(a => a.Text));
        // Stopped, it doesn't answer there; on no named network, nothing calls it by name.
        Assert.Equal("Stopped", ContainerAddressing.Shown(postgres, ContainerRole.Database, [Note(9187)], "10.1.1.80", named: "postgres", running: false)[2].Note);
        Assert.Equal(2, ContainerAddressing.Shown(postgres, ContainerRole.Database, [Note(9187)], "10.1.1.80", named: null).Count);
    }

    [Fact]
    public void A_noted_port_is_reached_on_the_containers_own_lan_address_too()
    {
        // Its own address answers on every port it listens on: a noted one too, a web one as a link.
        List<Address> postgres = [Internal("postgres:5432", 5432), OwnLan("10.1.1.230:5432", 5432)];
        var shown = ContainerAddressing.Shown(postgres, ContainerRole.Database, [Note(5432), Note(9187)], "10.1.1.80", named: "postgres");
        Assert.Equal(["10.1.1.230:5432", "10.1.1.230:9187"], shown.Where(a => a.Kind == AddressKind.OwnLan).Select(a => a.Text));
        Assert.Equal(9187, shown.Last(a => a.Kind == AddressKind.OwnLan).ContainerPort);
        var web = ContainerAddressing.Shown([OwnLan("10.1.1.230:9000", 9000)], ContainerRole.Other, [Note(8080)], "10.1.1.80");
        Assert.Equal(("http://10.1.1.230:8080", "http://10.1.1.230:8080"), (web[1].Text, web[1].Href));

        // With no port known it's the bare address, until one is noted; on no named network as well.
        List<Address> bare = [new(AddressKind.OwnLan, "10.1.1.231", null, "10.1.1.231", null, "tcp", false, "Stopped")];
        var noted = ContainerAddressing.Shown(bare, ContainerRole.Other, [Note(1883)], "10.1.1.80", running: false);
        var only = Assert.Single(noted);
        Assert.Equal(("10.1.1.231:1883", (int?)1883, false, "Stopped"), (only.Text, only.ContainerPort, only.Live, only.Note));
        Assert.Equal(bare, ContainerAddressing.Shown(bare, ContainerRole.Other, [], "10.1.1.80"));
    }

    [Fact]
    public void A_container_on_the_hosts_network_is_reached_on_the_ports_noted_for_it()
    {
        List<Address> tunnel = [new(AddressKind.Lan, "10.1.1.80", null, "10.1.1.80", null, "tcp", true, "On any port it listens on (host network)")];
        var shown = ContainerAddressing.Shown(tunnel, ContainerRole.Tunnel, [Note(8080), Note(53, "udp")], "10.1.1.80", hostNetwork: true);

        Assert.Equal(["10.1.1.80", "10.1.1.80:53", "http://10.1.1.80:8080"], shown.Select(a => a.Text));
        Assert.Equal(("http://10.1.1.80:8080", (int?)8080), (shown[2].Href, shown[2].ContainerPort));
        Assert.Null(shown[1].Href);
        // Without notes it's the one line, as Docker can't say which ports it listens on.
        Assert.Equal(tunnel, ContainerAddressing.Shown(tunnel, ContainerRole.Tunnel, [], "10.1.1.80", hostNetwork: true));
        // Stopped, they're there to copy, not to open.
        var stopped = ContainerAddressing.Shown([tunnel[0] with { Live = false }], ContainerRole.Tunnel, [Note(8080)], "10.1.1.80", hostNetwork: true);
        Assert.Equal((false, (string?)null, "Stopped"), (stopped[1].Live, stopped[1].Href, stopped[1].Note));
    }

    [Fact]
    public async Task Notes_are_saved_and_taken_away_per_container()
    {
        var notes = new ContainerPortNotes(_db);
        await notes.SaveAsync("cloudflare-tunnel", [(20241, "tcp"), (8080, "tcp")]);
        await notes.SaveAsync("postgres", [(8081, "tcp")]);

        Assert.Equal([(8080, "tcp"), (20241, "tcp")], (await notes.ListAsync("cloudflare-tunnel")).Select(n => (n.Port, n.Protocol)));
        // Saving the container's notes again replaces them; another container's stay.
        await notes.SaveAsync("cloudflare-tunnel", [(8080, "tcp"), (53, "udp"), (53, "udp")]);
        Assert.Equal([(53, "udp"), (8080, "tcp")], (await notes.ListAsync("cloudflare-tunnel")).Select(n => (n.Port, n.Protocol)));
        Assert.Equal([(8081, "tcp")], (await notes.ListAsync("postgres")).Select(n => (n.Port, n.Protocol)));
        await notes.SaveAsync("postgres", []);
        Assert.Empty(await notes.ListAsync("postgres"));
    }
}
