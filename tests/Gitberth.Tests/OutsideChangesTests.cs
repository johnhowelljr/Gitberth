using System.Text.Json.Nodes;
using Gitberth.Audit;
using Gitberth.Docker;

namespace Gitberth.Tests;

/// <summary>
/// Docker changes made outside Gitberth: what Gitberth's own commands and calls expect, how Docker's events read, and which
/// of them become entries (one per change, never a crash, Docker's own restart, Gitberth's own work or its helpers).
/// </summary>
public sealed class OutsideChangesTests
{
    // Events happen at T0 and after; the clock (_now) is what Gitberth reads, and moves on its own.
    private static readonly DateTime T0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private DateTime _now = T0;

    private ExpectedChanges Expected() => new() { Now = () => _now };

    private static DockerChange Event(DateTime at, string type, string action, string id, string name, params (string Key, string Value)[] attributes) =>
        new(at, type, action, id, name, attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    private static DockerChange Container(double seconds, string action, string name = "postgres", string id = "c0ffee000000aaaa", params (string, string)[] attributes) =>
        Event(T0.AddSeconds(seconds), "container", action, id, name, [("name", name), .. attributes]);

    // ---- What Gitberth expects ----

    [Fact]
    public void A_command_is_Gitberths_own_while_it_runs_and_a_little_after_by_the_events_own_time()
    {
        var expected = Expected();
        var lease = expected.ForCli(["stop", "-t", "20", "postgres"]);
        _now = _now.AddSeconds(8);
        Assert.True(expected.Matches(Container(0, "kill", attributes: ("signal", "15"))));
        Assert.True(expected.Matches(Container(1, "die")));
        Assert.True(expected.Matches(Container(2, "stop")));
        Assert.False(expected.Matches(Container(2, "start")));
        Assert.False(expected.Matches(Container(2, "stop", "redis", "beef00000000")));
        lease.Dispose();

        // Within the grace after it ended, still its own; an event from long after is someone else's.
        Assert.True(expected.Matches(Container(10, "stop")));
        Assert.False(expected.Matches(Container(8 + ExpectedChanges.Grace.TotalSeconds + 5, "stop")));
        // Replayed after the stream was away, an event from the command's time still matches.
        _now = _now.AddMinutes(10);
        Assert.True(expected.Matches(Container(2, "stop")));
        // Long forgotten after that.
        _now = _now.AddMinutes(30);
        Assert.False(expected.Matches(Container(2, "stop")));
        Assert.Equal(0, expected.Count);
    }

    [Fact]
    public void A_short_ID_names_the_container_it_begins_and_a_rename_matches_by_either_name()
    {
        var expected = Expected();
        using (expected.ForCli(["rm", "-f", "c0ffee000000"]))
            Assert.True(expected.Matches(Container(0, "destroy", "anything", "c0ffee000000aaaabbbb")));
        using (expected.ForCli(["rename", "gb-web", "gb-web_prev"]))
        {
            Assert.True(expected.Matches(Container(0, "rename", "gb-web_prev", attributes: ("oldName", "/gb-web"))));
            Assert.False(expected.Matches(Container(0, "rename", "db2", attributes: ("oldName", "/db"))));
        }
    }

    [Fact]
    public void Bulk_work_expects_by_prefix_or_anything_of_its_type()
    {
        var expected = Expected();
        using (expected.ForCli(["image", "prune", "-f", "--filter", "label=gitberth.app=web"]))
        {
            Assert.True(expected.Matches(Event(_now, "image", "delete", "sha256:abc", "sha256:abc")));
            Assert.False(expected.Matches(Event(_now, "image", "pull", "nginx:1.27", "nginx")));
        }
        using (expected.ForCli(["compose", "-p", "gb-shop", "up", "-d"]))
        {
            // The project's own: its label, or its <project>_ names (as Gitberth names every member).
            Assert.True(expected.Matches(Container(0, "create", "gb-shop-web-1", attributes: ("com.docker.compose.project", "gb-shop"))));
            Assert.True(expected.Matches(Container(0, "create", "gb-shop_web")));
            Assert.True(expected.Matches(Event(_now, "network", "create", "n1", "gb-shop_default", ("name", "gb-shop_default"))));
            Assert.False(expected.Matches(Container(0, "create", "other-web-1")));
            // Never another project whose name only starts the same.
            Assert.False(expected.Matches(Container(0, "stop", "gb-shopify_web")));
            Assert.False(expected.Matches(Container(0, "stop", "gb-shop-pr-1_web", attributes: ("com.docker.compose.project", "gb-shop-pr-1"))));
            Assert.False(expected.Matches(Event(_now, "network", "destroy", "n2", "gb-shopify_default", ("name", "gb-shopify_default"))));
        }
        using (expected.ExpectPrefix("volume", ["destroy"], "gb-web-"))
            Assert.True(expected.Matches(Event(_now, "volume", "destroy", "gb-web-data", "gb-web-data")));
    }

    [Fact]
    public void Each_mutating_command_and_call_says_what_it_changes_and_reads_change_nothing()
    {
        string Specs(IEnumerable<ExpectedChanges.Spec> specs) => string.Join(" ", specs.Select(s => $"{s.Type}:{string.Join(',', s.Actions)}:{(s.Any ? "*" : s.Prefix ?? string.Join(',', s.Subjects))}"));

        Assert.Equal("container:create,start,die,kill,stop,destroy:gb-web volume:create:* image:pull,tag,untag:*",
            Specs(ExpectedChanges.CliExpectations(["run", "-d", "--name", "gb-web", "-e", "A=1", "gitberth/web:abc"])));
        Assert.Equal("container:update:gb-web", Specs(ExpectedChanges.CliExpectations(["update", "--restart", "always", "gb-web"])));
        Assert.Equal("container:kill,die,stop,start,restart:gb-web", Specs(ExpectedChanges.CliExpectations(["restart", "-t", "20", "gb-web"])));
        Assert.Equal("network:connect:gitberth", Specs(ExpectedChanges.CliExpectations(["network", "connect", "--gw-priority", "-1", "gitberth", "gb-web"])));
        Assert.Equal("network:connect:gitberth", Specs(ExpectedChanges.CliExpectations(["network", "connect", "--alias", "web", "gitberth", "gb-web"])));
        Assert.Equal("network:create:gitberth", Specs(ExpectedChanges.CliExpectations(["network", "create", "--label", "gitberth.managed=true", "gitberth"])));
        Assert.Equal("volume:destroy:gb-web-data", Specs(ExpectedChanges.CliExpectations(["volume", "rm", "gb-web-data"])));
        Assert.Equal("image:pull,tag,untag,delete,load,import:*", Specs(ExpectedChanges.CliExpectations(["build", "-t", "gitberth/web:abc", "."])));
        Assert.Equal("image:untag,delete:*", Specs(ExpectedChanges.CliExpectations(["image", "rm", "gitberth/web:old"])));
        foreach (var read in new[] { new[] { "ps", "-a" }, ["container", "inspect", "gb-web"], ["logs", "--tail", "50", "gb-web"], ["image", "ls"], ["version"], ["cp", "x:/a", "-"],
                     ["volume", "ls", "--quiet"], ["network", "inspect", "gitberth"], ["exec", "db", "psql"], ["info"] })
            Assert.Empty(ExpectedChanges.CliExpectations(read));

        Assert.Equal("container:create:gb-web_new_abc volume:create:*",
            Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "containers/create?name=gb-web_new_abc", null)));
        Assert.Equal("container:rename:3f2a9b1c,gb-web", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "containers/3f2a9b1c/rename?name=gb-web", null)));
        Assert.Equal("container:kill,die,stop,destroy:gb-web_old_1", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Delete, "containers/gb-web_old_1?force=false", null)));
        Assert.Equal("container:kill,die,stop:gb-web", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "containers/gb-web/stop?t=20", null)));
        Assert.Equal("network:create:gitberth-lan", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "networks/create", new JsonObject { ["Name"] = "gitberth-lan" })));
        Assert.Equal("network:disconnect:gitberth", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "networks/gitberth/disconnect", null)));
        Assert.Equal("image:pull,tag,untag:*", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Post, "images/create?fromImage=nginx&tag=1.27", null)));
        Assert.Equal("image:untag,delete:*", Specs(ExpectedChanges.ApiExpectations(HttpMethod.Delete, "images/sha256%3Aabc", null)));
        Assert.Empty(ExpectedChanges.ApiExpectations(HttpMethod.Get, "containers/json?all=true", null));
        Assert.Empty(ExpectedChanges.ApiExpectations(HttpMethod.Post, "containers/gb-web/wait?condition=not-running", null));
        Assert.Empty(ExpectedChanges.ApiExpectations(HttpMethod.Post, "auth", new JsonObject()));
    }

    // ---- Reading Docker's events ----

    [Fact]
    public void Events_of_every_type_are_read_with_their_names_and_times()
    {
        var stop = DockerChange.Parse("""{"Type":"container","Action":"stop","Actor":{"ID":"c0ffee","Attributes":{"name":"postgres","image":"postgres:18"}},"time":1763121600,"timeNano":1763121600123456789}""")!;
        Assert.Equal(("container", "stop", "c0ffee", "postgres"), (stop.Type, stop.Action, stop.Id, stop.Name));
        Assert.Equal(new DateTime(2025, 11, 14, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567), stop.Time);
        Assert.Equal("postgres:18", stop.Attributes["image"]);

        var exec = DockerChange.Parse("""{"Type":"container","Action":"exec_start: sh -c date","Actor":{"ID":"c0ffee","Attributes":{"name":"db"}},"time":1763121600}""")!;
        Assert.Equal("exec_start", exec.Action);
        var pull = DockerChange.Parse("""{"Type":"image","Action":"pull","Actor":{"ID":"nginx:1.27","Attributes":{"name":"nginx"}},"time":1763121600}""")!;
        Assert.Equal(("image", "nginx"), (pull.Type, pull.Name));
        var join = DockerChange.Parse("""{"Type":"network","Action":"connect","Actor":{"ID":"n1","Attributes":{"container":"c0ffee","name":"backend","type":"bridge"}},"time":1763121600}""")!;
        Assert.Equal(("network", "backend", "c0ffee"), (join.Type, join.Name, join.Attributes["container"]));
        var volume = DockerChange.Parse("""{"Type":"volume","Action":"create","Actor":{"ID":"pgdata","Attributes":{"driver":"local"}},"time":1763121600}""")!;
        Assert.Equal(("volume", "pgdata"), (volume.Type, volume.Name));
        // An old engine's line without Type, and lines that can't be read.
        Assert.Equal("container", DockerChange.Parse("""{"status":"start","id":"c0ffee","Actor":{"ID":"c0ffee","Attributes":{"image":"redis","name":"cache"}},"time":1763121600}""")!.Type);
        Assert.Null(DockerChange.Parse("not json"));
        Assert.Null(DockerChange.Parse("""{"Type":"container","Action":"start"}"""));
        Assert.Null(DockerChange.Parse(""));
    }

    // ---- Which become entries ----

    private (OutsideChangeBuffer Buffer, ExpectedChanges Expected) Buffer(string? self = "5e1f00000000")
    {
        var expected = Expected();
        return (new OutsideChangeBuffer(expected, c => self is not null && c.Id.StartsWith(self, StringComparison.Ordinal)), expected);
    }

    private List<OutsideChange> Run(OutsideChangeBuffer buffer, params DockerChange[] changes)
    {
        foreach (var c in changes) buffer.Feed(c, _now);
        return buffer.Flush(_now.AddMinutes(1));
    }

    [Fact]
    public void A_stop_made_with_docker_on_the_host_is_one_entry_and_a_crash_is_none()
    {
        var (buffer, _) = Buffer();
        var stopped = Run(buffer, Container(0, "kill", attributes: ("signal", "15")), Container(1, "die", attributes: ("exitCode", "0")), Container(1, "stop"));
        var entry = Assert.Single(stopped);
        Assert.Equal("docker.container.stopped", entry.Action);
        Assert.Equal("Stopped outside Gitberth: postgres.", entry.Summary);
        Assert.Equal("postgres", entry.Container);

        // A crash, and Docker's own restart of it: the Health page's, not a change.
        Assert.Empty(Run(buffer, Container(100, "die", attributes: ("exitCode", "1")), Container(101, "start")));
        // A crash loop's restarts a minute apart (Docker's back-off) are its own too.
        Assert.Empty(Run(buffer, Container(160, "die", attributes: ("exitCode", "1")), Container(220, "start")));
        Assert.Empty(Run(buffer, Container(230, "oom")));
    }

    [Fact]
    public void A_start_after_a_stop_is_someones_even_soon_after()
    {
        var (buffer, _) = Buffer();
        var changes = Run(buffer, Container(0, "kill", attributes: ("signal", "15")), Container(0.5, "die"), Container(0.6, "stop"));
        Assert.Equal(["docker.container.stopped"], changes.Select(c => c.Action));
        Assert.Equal(["docker.container.started"], Run(buffer, Container(5, "start")).Select(c => c.Action));
    }

    [Fact]
    public void A_start_after_a_stop_is_someones_when_docker_says_stop_before_the_death()
    {
        // docker stop's events as a busy engine can send them: the stop's own event before the death it caused.
        var (buffer, _) = Buffer();
        var changes = Run(buffer, Container(0, "kill", attributes: ("signal", "15")), Container(0.4, "stop"), Container(0.5, "die", attributes: ("exitCode", "0")));
        Assert.Equal(["docker.container.stopped"], changes.Select(c => c.Action));
        Assert.Equal(["Started outside Gitberth: postgres."], Run(buffer, Container(8, "start")).Select(c => c.Summary));
        // A crash later on is still a crash, and Docker's restart of it nobody's change.
        Assert.Empty(Run(buffer, Container(300, "die", attributes: ("exitCode", "1")), Container(300.2, "start")));
    }

    [Fact]
    public void A_restart_a_run_and_a_removal_are_one_entry_each()
    {
        var (buffer, _) = Buffer();
        var restart = Run(buffer, Container(0, "kill"), Container(0, "die"), Container(1, "stop"), Container(1, "start"), Container(1, "restart"));
        Assert.Equal(["docker.container.restarted"], restart.Select(c => c.Action));

        var run = Run(buffer, Container(10, "create", "web2", "d00d00000000", ("image", "nginx:1.27")), Container(10.2, "start", "web2", "d00d00000000"));
        var created = Assert.Single(run);
        Assert.Equal("Created and started outside Gitberth: web2.", created.Summary);
        Assert.Equal(("Image", "nginx:1.27"), (created.Changes[0].Field, created.Changes[0].To!));

        var removed = Run(buffer, Container(20, "kill", "web2", "d00d00000000"), Container(20, "die", "web2", "d00d00000000"), Container(20.1, "destroy", "web2", "d00d00000000"));
        Assert.Equal(["Removed outside Gitberth: web2."], removed.Select(c => c.Summary));

        var renamed = Assert.Single(Run(buffer, Container(30, "rename", "pg", attributes: ("oldName", "/postgres"))));
        Assert.Equal("Renamed outside Gitberth: postgres is pg now.", renamed.Summary);
        Assert.Equal(["docker.container.updated"], Run(buffer, Container(40, "update", "pg")).Select(c => c.Action));
    }

    [Fact]
    public void A_run_on_a_busy_engine_is_one_entry_though_its_start_comes_seconds_after_its_create()
    {
        var (buffer, _) = Buffer();
        buffer.Feed(Container(0, "create", "web3", "d00d00000001", ("image", "nginx:1.27")), _now);
        // While the create waits for its start, nothing is said yet.
        Assert.Empty(buffer.Flush(_now.AddSeconds(6)));
        buffer.Feed(Container(8, "start", "web3", "d00d00000001"), _now.AddSeconds(8));
        var created = Assert.Single(buffer.Flush(_now.AddSeconds(30)));
        Assert.Equal("Created and started outside Gitberth: web3.", created.Summary);

        // An ordinary run, its start right after its create, is said as soon as it settles, not after the create's longer wait.
        buffer.Feed(Container(50, "create", "web5", "d00d00000003", ("image", "nginx:1.27")), _now.AddSeconds(50));
        buffer.Feed(Container(50.2, "start", "web5", "d00d00000003"), _now.AddSeconds(50.2));
        Assert.Equal(["Created and started outside Gitberth: web5."],
            buffer.Flush(_now.AddSeconds(50.2) + OutsideChangeBuffer.Settle + TimeSpan.FromMilliseconds(100)).Select(c => c.Summary));

        // A builder's intermediate container, made and removed at once, is said as soon as it settles too.
        buffer.Feed(Container(70, "create", "goofy_bouman", "d00d00000004"), _now.AddSeconds(70));
        buffer.Feed(Container(70.5, "destroy", "goofy_bouman", "d00d00000004"), _now.AddSeconds(70.5));
        Assert.Equal(["Created outside Gitberth: goofy_bouman.", "Removed outside Gitberth: goofy_bouman."],
            buffer.Flush(_now.AddSeconds(70.5) + OutsideChangeBuffer.Settle + TimeSpan.FromMilliseconds(100)).Select(c => c.Summary));

        // A create nothing starts is said on its own once it has waited.
        buffer.Feed(Container(100, "create", "web4", "d00d00000002", ("image", "nginx:1.27")), _now.AddSeconds(100));
        Assert.Empty(buffer.Flush(_now.AddSeconds(105)));
        Assert.Equal(["Created outside Gitberth: web4."], buffer.Flush(_now.AddSeconds(100) + OutsideChangeBuffer.CreateSettle).Select(c => c.Summary));
    }

    [Fact]
    public void A_kill_waits_long_enough_to_see_whether_docker_stop_follows()
    {
        var (buffer, _) = Buffer();
        buffer.Feed(Container(0, "kill", attributes: ("signal", "15")), _now);
        Assert.Empty(buffer.Flush(_now.AddSeconds(5)));
        buffer.Feed(Container(10, "kill", attributes: ("signal", "9")), _now.AddSeconds(10));
        buffer.Feed(Container(10.2, "die"), _now.AddSeconds(10.2));
        buffer.Feed(Container(10.3, "stop"), _now.AddSeconds(10.3));
        var stopped = buffer.Flush(_now.AddSeconds(30));
        Assert.Equal(["docker.container.stopped"], stopped.Select(c => c.Action));

        var killed = Run(buffer, Container(60, "kill", attributes: ("signal", "9")), Container(60, "die", attributes: ("exitCode", "137")));
        Assert.Equal(["Killed outside Gitberth: postgres (signal 9)."], killed.Select(c => c.Summary));
    }

    [Fact]
    public void Gitberths_own_changes_its_container_and_its_helpers_are_never_outside()
    {
        var (buffer, expected) = Buffer();
        using (expected.ForCli(["stop", "-t", "20", "postgres"]))
            Assert.Empty(Run(buffer, Container(0, "kill"), Container(0, "die"), Container(1, "stop")));
        Assert.Empty(Run(buffer, Container(5, "restart", "gitberth", "5e1f00000000abcd")));
        Assert.Empty(Run(buffer, Container(6, "create", "gitberth-hostscan-1", "aaaa00000000", (HostScanHelper.Label, "hostscan"))));
        Assert.Empty(Run(buffer, Container(6, "exec_create"), Container(6, "health_status")));
    }

    [Fact]
    public void Joining_a_network_counts_only_when_it_isnt_part_of_a_start_or_a_stop()
    {
        var (buffer, _) = Buffer();
        // docker start: Docker connects the container's networks, then says start.
        var start = Run(buffer, Event(_now, "network", "connect", "n1", "backend", ("container", "c0ffee000000aaaa"), ("name", "backend")), Container(0.3, "start"));
        Assert.Equal(["docker.container.started"], start.Select(c => c.Action));

        var joined = Run(buffer, Event(_now.AddSeconds(60), "network", "connect", "n1", "backend", ("container", "c0ffee000000aaaa"), ("name", "backend")));
        var entry = Assert.Single(joined);
        Assert.Equal("Connected postgres to the network backend outside Gitberth.", entry.Summary);
        Assert.Equal(("docker.network.connected", "postgres", "backend"), (entry.Action, entry.Container!, entry.Target!));

        var made = Run(buffer, Event(_now.AddSeconds(70), "network", "create", "n2", "frontend", ("name", "frontend")),
            Event(_now.AddSeconds(71), "network", "destroy", "n2", "frontend", ("name", "frontend")));
        Assert.Equal(["docker.network.created", "docker.network.removed"], made.Select(c => c.Action));
    }

    [Fact]
    public void A_restart_through_gitberth_is_its_own_with_the_network_joins_docker_makes_for_it()
    {
        // docker restart's events as Docker 29 sends them, word for word: the stop before the death, and the container's
        // network left and joined again on the way.
        const string id = "16c97ee887153314b984776e87efb5848ec672542e106ba3aa148d60e440e94c";
        const string net = "9ec02d47d46dd631958aa37ecfec105c5e980efab92b4f54b73d54543830ca5b";
        string C(string action, long nanos, string extra = "") =>
            $$$"""{"Type":"container","Action":"{{{action}}}","Actor":{"ID":"{{{id}}}","Attributes":{"image":"python:3.12-alpine","name":"gb-rc"{{{extra}}}}},"scope":"local","time":{{{nanos / 1_000_000_000}}},"timeNano":{{{nanos}}}}""";
        string N(string action, long nanos) =>
            $$$"""{"Type":"network","Action":"{{{action}}}","Actor":{"ID":"{{{net}}}","Attributes":{"container":"{{{id}}}","name":"gitberth","type":"bridge"}},"scope":"local","time":{{{nanos / 1_000_000_000}}},"timeNano":{{{nanos}}}}""";
        var t0 = 1794643398_696096176L;
        var lines = new[]
        {
            C("kill", t0, ""","signal":"15" """.Trim()), C("kill", t0 + 1_016_154_419, ""","signal":"9" """.Trim()), N("disconnect", t0 + 1_106_607_394),
            C("stop", t0 + 1_107_620_536), C("die", t0 + 1_111_636_180, ""","exitCode":"137" """.Trim()), N("connect", t0 + 1_262_427_270),
            C("start", t0 + 1_283_673_000), C("restart", t0 + 1_283_774_000),
        };
        var start = DateTime.UnixEpoch.AddTicks(t0 / 100);
        _now = start.AddSeconds(-0.5);
        var (buffer, expected) = Buffer();
        using (expected.ForCli(["restart", "gb-rc"]))
        {
            foreach (var line in lines)
            {
                _now = DockerChange.Parse(line)!.Time.AddMilliseconds(20);
                buffer.Feed(DockerChange.Parse(line)!, _now);
            }
        }
        Assert.Empty(buffer.Flush(_now.AddMinutes(1)));
    }

    // ---- Network joins and leaves on a busy engine: events captured from rig a's Docker 29 (docker events --format json) ----

    private const string AppId = "9e099584f7d4c3e1a8b2f0d6e4c2a1b3c5d7e9f1a3b5c7d9e1f3a5b7c9d1e3f5";
    private const string GitberthNet = "8a8d15c5ef5b3c4ad9b2a7e6f1c0d3e2b5a4c7d6e9f8a1b0c3d2e5f4a7b6c9d8";

    // One event as the engine sends it, at a time given as seconds after 13:57:00 (all on 2026-11-14 UTC).
    private static string Raw(double at, string type, string action, string extra = "")
    {
        var nanos = new DateTimeOffset(2026, 11, 14, 13, 57, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L + (long)Math.Round(at * 1e9);
        var actor = type == "network"
            ? $$$"""{"ID":"{{{GitberthNet}}}","Attributes":{"container":"{{{AppId}}}","name":"gitberth","type":"bridge"}}"""
            : $$$"""{"ID":"{{{AppId}}}","Attributes":{"image":"gitberth/ld-a81a:7e9bb7c","name":"gb-ld-a81a"{{{extra}}}}}""";
        return $$$"""{"Type":"{{{type}}}","Action":"{{{action}}}","Actor":{{{actor}}},"scope":"local","time":{{{nanos / 1_000_000_000}}},"timeNano":{{{nanos}}}}""";
    }

    // The events read as OutsideChanges reads them: each comes in 20 ms after it happened, and what's settled is flushed
    // every second, as its timer does. The command, when there is one, holds its lease from just before the event it
    // starts at (the first, unless said) until just after the last, as DockerCli does. Returns every entry, flushed to the end.
    private List<OutsideChange> Stream(string[] lines, IReadOnlyList<string>? command, int commandStarts = 0)
    {
        var events = lines.Select(l => DockerChange.Parse(l)!).ToList();
        _now = events[0].Time.AddMilliseconds(-200);
        var (buffer, expected) = Buffer();
        IDisposable? lease = null;
        var said = new List<OutsideChange>();
        var flushAt = _now.AddSeconds(1);
        for (var i = 0; i < events.Count; i++)
        {
            var arrives = events[i].Time.AddMilliseconds(20);
            for (; flushAt <= arrives; flushAt = flushAt.AddSeconds(1))
            {
                _now = flushAt;
                said.AddRange(buffer.Flush(_now));
            }
            if (command is not null && i == commandStarts)
            {
                _now = events[i].Time.AddMilliseconds(-200);
                lease = expected.ForCli(command);
            }
            _now = arrives;
            buffer.Feed(events[i], _now);
        }
        _now = _now.AddMilliseconds(100);
        lease?.Dispose();
        for (var end = _now.AddMinutes(2); flushAt <= end; flushAt = flushAt.AddSeconds(1))
        {
            _now = flushAt;
            said.AddRange(buffer.Flush(_now));
        }
        return said;
    }

    // Gitberth's Restart (docker restart -t 20) of a Git app on a busy engine, as captured on rig a: the leave comes with the
    // program's exit, the container's files are mounted again for 22.7 s, and only then the join comes, here with the start
    // 6.1 s after it (the gap in rig a's misattributed join): no event of the container is near the join on either side.
    private static string[] SlowRestart() =>
    [
        Raw(10.118934, "container", "kill", ""","signal":"15" """.Trim()),
        Raw(10.891255, "network", "disconnect"),
        Raw(10.891833, "container", "stop"),
        Raw(10.898017, "container", "die", ""","exitCode":"0" """.Trim()),
        Raw(33.596582, "network", "connect"),
        Raw(39.712745, "container", "start"),
        Raw(39.712909, "container", "restart"),
    ];

    // Another captured restart, with its files mounted again in 5.1 s and its start 4.5 s after the join: short enough for
    // the restart's events to be one change.
    private static string[] BusyRestart() =>
    [
        Raw(35.779760, "container", "kill", ""","signal":"15" """.Trim()),
        Raw(36.585898, "network", "disconnect"),
        Raw(36.588297, "container", "stop"),
        Raw(36.591809, "container", "die", ""","exitCode":"0" """.Trim()),
        Raw(41.700000, "network", "connect"),
        Raw(46.200000, "container", "start"),
        Raw(46.200150, "container", "restart"),
    ];

    // A Delete (docker rm -f) of a running app on a busy engine: the leave 6.1 s after the kill, and the death 4.5 s after
    // the leave (rig a's misattributed leave came 20 s before the deletion was recorded).
    private static string[] BusyRemoval() =>
    [
        Raw(36.571623, "container", "kill", ""","signal":"9" """.Trim()),
        Raw(42.682112, "network", "disconnect"),
        Raw(47.214371, "container", "die", ""","exitCode":"137" """.Trim()),
        Raw(47.306235, "container", "destroy"),
    ];

    [Fact]
    public void Gitberths_restart_and_removal_on_a_busy_engine_make_no_network_entry()
    {
        Assert.Empty(Stream(SlowRestart(), ["restart", "-t", "20", "gb-ld-a81a"]));
        Assert.Empty(Stream(BusyRestart(), ["restart", "-t", "20", "gb-ld-a81a"]));
        Assert.Empty(Stream(BusyRemoval(), ["rm", "-f", "gb-ld-a81a"]));
        // As captured, with nothing stretched: Stop's leave 5.5 s after its kill, its stop 1.9 s after the leave, and Start.
        Assert.Empty(Stream(
        [
            Raw(31.327094, "container", "kill", ""","signal":"15" """.Trim()),
            Raw(36.871368, "network", "disconnect"),
            Raw(38.791502, "container", "stop"),
            Raw(39.355778, "container", "die", ""","exitCode":"0" """.Trim()),
        ], ["stop", "-t", "20", "gb-ld-a81a"]));
        Assert.Empty(Stream([Raw(42.057549, "network", "connect"), Raw(42.400733, "container", "start")], ["start", "gb-ld-a81a"]));
    }

    [Fact]
    public void The_same_restart_and_removal_made_outside_Gitberth_are_one_entry_each_with_no_network_entry()
    {
        Assert.Equal(["docker.container.restarted"], Stream(BusyRestart(), command: null).Select(c => c.Action));
        Assert.Equal(["docker.container.removed"], Stream(BusyRemoval(), command: null).Select(c => c.Action));
        // However long a restart takes, its join and leave are never entries of their own.
        Assert.DoesNotContain(Stream(SlowRestart(), command: null), c => c.Action.StartsWith("docker.network.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_join_waits_for_its_containers_next_event_and_a_start_that_comes_late_is_still_its_own()
    {
        var (buffer, _) = Buffer();
        buffer.Feed(DockerChange.Parse(Raw(33.596582, "network", "connect"))!, _now);
        // Nothing yet to tell by: it waits rather than guess.
        Assert.Empty(buffer.Flush(_now.AddSeconds(10)));
        buffer.Feed(DockerChange.Parse(Raw(45.0, "container", "start"))!, _now.AddSeconds(11));
        Assert.Equal(["docker.container.started"], buffer.Flush(_now.AddMinutes(1)).Select(c => c.Action));
    }

    [Fact]
    public void A_join_or_leave_made_with_docker_network_is_still_recorded_whatever_follows()
    {
        // docker network disconnect of a running container: nothing follows, and after the wait it's a change of its own.
        var (buffer, _) = Buffer();
        buffer.Feed(DockerChange.Parse(Raw(0, "container", "start"))!, _now);
        buffer.Feed(DockerChange.Parse(Raw(60, "network", "disconnect"))!, _now);
        Assert.Equal(["docker.container.started"], buffer.Flush(_now.AddSeconds(5)).Select(c => c.Action));
        var left = Assert.Single(buffer.Flush(_now + OutsideChangeBuffer.JoinWait + TimeSpan.FromSeconds(1)));
        Assert.Equal(("docker.network.disconnected", "Disconnected gb-ld-a81a from the network gitberth outside Gitberth."), (left.Action, left.Summary));

        // A leave and then a docker stop: the stop's kill comes after the leave, so the container was still running.
        Assert.Equal(["docker.network.disconnected", "docker.container.stopped"], Stream(
        [
            Raw(1.0, "network", "disconnect"),
            Raw(3.0, "container", "kill", ""","signal":"15" """.Trim()),
            Raw(3.2, "container", "stop"),
            Raw(3.3, "container", "die", ""","exitCode":"0" """.Trim()),
        ], command: null).Select(c => c.Action));

        // A join made outside, then a restart of the container a few seconds later: the join is still outside, whoever
        // restarts it; Gitberth's restart is nobody's entry, and one made outside is its own.
        string[] joinedThenRestarted =
        [
            Raw(1.0, "network", "connect"),
            Raw(4.0, "container", "kill", ""","signal":"15" """.Trim()),
            Raw(4.5, "network", "disconnect"),
            Raw(4.6, "container", "stop"),
            Raw(4.7, "container", "die", ""","exitCode":"0" """.Trim()),
            Raw(5.5, "network", "connect"),
            Raw(5.6, "container", "start"),
            Raw(5.6, "container", "restart"),
        ];
        Assert.Equal(["docker.network.connected"], Stream(joinedThenRestarted, ["restart", "-t", "20", "gb-ld-a81a"], commandStarts: 1).Select(c => c.Action));
        Assert.Equal(["docker.network.connected", "docker.container.restarted"], Stream(joinedThenRestarted, command: null).Select(c => c.Action));

        // The summary names the container by what it was called when it joined.
        var joined = Stream([Raw(1.0, "container", "start"), Raw(80.0, "network", "connect")], command: null);
        Assert.Equal("Connected gb-ld-a81a to the network gitberth outside Gitberth.", joined.Single(c => c.Action == "docker.network.connected").Summary);
    }

    [Fact]
    public void A_crash_and_dockers_restart_of_it_are_nobodys_change_network_joins_included()
    {
        // The program exits by itself: it leaves its networks, dies, and Docker's restart policy starts it again.
        Assert.Empty(Stream(
        [
            Raw(1.0, "network", "disconnect"),
            Raw(4.5, "container", "die", ""","exitCode":"1" """.Trim()),
            Raw(25.0, "network", "connect"),
            Raw(31.0, "container", "start"),
        ], command: null));
        // An older engine says the death before the leave: after a crash, and in a docker rm -f, whose next event is the removal.
        var (buffer, _) = Buffer();
        buffer.Feed(DockerChange.Parse(Raw(0, "container", "die", ""","exitCode":"1" """.Trim()))!, _now);
        buffer.Feed(DockerChange.Parse(Raw(0.2, "network", "disconnect"))!, _now);
        Assert.Empty(buffer.Flush(_now.AddMinutes(2)));
        Assert.Equal(["docker.container.removed"], Stream(
        [
            Raw(1.0, "container", "kill", ""","signal":"9" """.Trim()),
            Raw(1.1, "container", "die", ""","exitCode":"137" """.Trim()),
            Raw(1.3, "network", "disconnect"),
            Raw(1.4, "container", "destroy"),
        ], command: null).Select(c => c.Action));
    }

    [Fact]
    public void Images_and_volumes_are_said_once_each_and_anonymous_volumes_go_with_their_containers()
    {
        var (buffer, _) = Buffer();
        var removed = Run(buffer, Event(_now, "image", "untag", "sha256:abc123def4567890", "postgres:17"), Event(_now, "image", "delete", "sha256:abc123def4567890", "sha256:abc123def4567890"));
        Assert.Equal(["Removed the image postgres:17 outside Gitberth."], removed.Select(c => c.Summary));
        Assert.Equal(["docker.image.pulled"], Run(buffer, Event(_now, "image", "pull", "redis:8", "redis")).Select(c => c.Action));
        Assert.Equal(["docker.volume.created"], Run(buffer, Event(_now, "volume", "create", "pgdata", "pgdata")).Select(c => c.Action));
        Assert.Empty(Run(buffer, Event(_now, "volume", "create", new string('a', 64), new string('a', 64))));
        Assert.Empty(Run(buffer, Event(_now, "volume", "mount", "pgdata", "pgdata")));
    }

    [Fact]
    public void Each_verb_has_its_past_tense()
    {
        Assert.Equal(["created", "removed", "renamed", "updated", "started", "stopped", "killed", "restarted", "paused", "unpaused", "pulled", "removed", "tagged", "untagged", "connected", "disconnected"],
            new[] { "create", "destroy", "rename", "update", "start", "stop", "kill", "restart", "pause", "unpause", "pull", "delete", "tag", "untag", "connect", "disconnect" }
                .Select(OutsideChangeBuffer.Verb));
    }
}
