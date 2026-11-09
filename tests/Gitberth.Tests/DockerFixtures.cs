using System.Text.Json.Nodes;

namespace Gitberth.Tests;

/// <summary>
/// Answers of the Docker Engine API and /proc files captured from the real host (CT 101, Docker 29.9.2, API 1.56), with
/// every secret replaced by REDACTED. Files: version, containers-json, networks-json, image-postgres, inspect-&lt;name&gt; for
/// postgres, gitberth, cloudflare-tunnel, gb-orb, gb-kanban, sample-bun-elysia, sample-bun-hono and sample-index (.json),
/// and proc-net-tcp, -tcp6, -udp, -udp6, -route, -fib_trie, -if_inet6 and proc-sys-net-ipv4-ip_local_port_range (.txt).
/// </summary>
public static class DockerFixtures
{
    public static string Text(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Docker", file));

    public static JsonNode Json(string file) => JsonNode.Parse(Text(file))!;

    /// <summary>A container's inspect, such as Inspect("postgres").</summary>
    public static JsonObject Inspect(string container) => Json($"inspect-{container}.json").AsObject();

    /// <summary>The host's /proc files, by their path on the host, as the "ports" helper prints them.</summary>
    public static Dictionary<string, string> ProcFiles() => new(StringComparer.Ordinal)
    {
        ["/proc/net/tcp"] = Text("proc-net-tcp.txt"),
        ["/proc/net/tcp6"] = Text("proc-net-tcp6.txt"),
        ["/proc/net/udp"] = Text("proc-net-udp.txt"),
        ["/proc/net/udp6"] = Text("proc-net-udp6.txt"),
        ["/proc/net/route"] = Text("proc-net-route.txt"),
        ["/proc/net/fib_trie"] = Text("proc-net-fib_trie.txt"),
        ["/proc/net/if_inet6"] = Text("proc-net-if_inet6.txt"),
        ["/proc/sys/net/ipv4/ip_local_port_range"] = Text("proc-sys-net-ipv4-ip_local_port_range.txt"),
    };
}
