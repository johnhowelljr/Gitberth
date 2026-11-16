using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Gitberth.Compose;

/// <summary>A port a service publishes or exposes, as its Compose file has it.</summary>
/// <param name="Published">The host port; null when it's only exposed, or Docker picks one.</param>
public sealed record ComposePort(int Target, int? Published, string Protocol = "tcp", string? HostIp = null);

/// <summary>A bind mount as the Compose file writes it, before Compose resolves it.</summary>
/// <param name="Source">As written: ./conf, ../data, /srv/photos, ${UPLOAD_LOCATION}.</param>
public sealed record ComposeBind(string Source, string Target, bool ReadOnly);

/// <summary>One service of a Compose file, as Gitberth reads it to pick the web service and to show it.</summary>
/// <param name="Builds">It has a build: section: Gitberth builds its image.</param>
/// <param name="Copies">How many containers it asks for (deploy.replicas, scale); 1 when it doesn't say.</param>
public sealed record ComposeServiceInfo(string Name, string? Image, bool Builds, IReadOnlyList<ComposePort> Ports, IReadOnlyList<ComposeBind> Binds,
    int Copies = 1, string? NetworkMode = null);

/// <summary>What Gitberth reads from a Compose file before Compose itself resolves it.</summary>
/// <param name="Error">Why it can't be read; null when it can.</param>
public sealed record ComposeFileInfo(IReadOnlyList<ComposeServiceInfo> Services, IReadOnlyList<ComposeVariable> Variables, string? Error)
{
    public ComposeServiceInfo? Service(string name) => Services.FirstOrDefault(s => s.Name == name);
}

/// <summary>The web service Gitberth picked and the container port it serves on (null when it can't tell).</summary>
public sealed record ComposeWeb(string Service, int? Port);

/// <summary>
/// Reads a Compose file the way Gitberth needs before deploying it: its services (image, build, ports, bind mounts, copies),
/// the variables it uses, and which service is the web one. Compose itself resolves the file at deploy time
/// (docker compose config); this is only for choosing and showing. Anchors and merge keys are followed.
/// </summary>
public static class ComposeFiles
{
    /// <summary>The Compose files in a folder, in Compose's own order of preference (compose.yaml first).</summary>
    public static List<string> Find(string folder) =>
        Directory.Exists(folder) ? ComposeNames.Files.Where(name => File.Exists(Path.Combine(folder, name))).ToList() : [];

    // The container ports a browser opens: HTTP servers, front-end dev servers and the apps self-hosters run most.
    private static readonly HashSet<int> WebPorts =
        [80, 8080, 3000, 8000, 5000, 4000, 8888, 8081, 8082, 8090, 9000, 3001, 5173, 4200, 2368, 8096, 8443, 443, 5678, 3030, 1337, 7860, 8501];

    // Ports of databases, caches and queues: never the web one.
    private static readonly HashSet<int> DataPorts = [5432, 3306, 33060, 27017, 6379, 5672, 15672, 9200, 9042, 8086, 1433, 11211, 4222, 2181, 9092];

    // Images known to serve the web on a port, when the file doesn't say it.
    private static readonly Dictionary<string, int> ImagePorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = 80, ["httpd"] = 80, ["caddy"] = 80, ["traefik"] = 80, ["ghost"] = 2368, ["wordpress"] = 80, ["nextcloud"] = 80,
        ["grafana"] = 3000, ["gitea"] = 3000, ["uptime-kuma"] = 3001, ["n8n"] = 5678, ["jellyfin"] = 8096, ["vaultwarden"] = 80,
        ["adminer"] = 8080, ["phpmyadmin"] = 80, ["mealie"] = 9000, ["miniflux"] = 8080, ["it-tools"] = 80,
    };

    private static readonly string[] WebNames = ["web", "app", "frontend", "front", "server", "site", "www", "ui", "nginx", "http", "proxy", "api", "caddy"];

    /// <summary>The file's services and variables; with <see cref="ComposeFileInfo.Error"/> when it can't be read.</summary>
    public static ComposeFileInfo Read(string yaml)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml ?? ""));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                return new ComposeFileInfo([], [], "That isn't a Compose file: it should start with services:.");
            root = mapping;
        }
        catch (YamlException ex)
        {
            return new ComposeFileInfo([], [], $"The YAML can't be read at line {ex.Start.Line}, column {ex.Start.Column}: {(ex.InnerException ?? ex).Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new ComposeFileInfo([], [], "The YAML can't be read: " + ex.Message);
        }
        var variables = new List<ComposeVariable>();
        foreach (var text in Scalars(root))
            foreach (var v in ComposeVariables.Used(text))
                if (variables.All(x => x.Name != v.Name)) variables.Add(v);
                else
                {
                    var at = variables.FindIndex(x => x.Name == v.Name);
                    if (variables[at].Default is null && v.Default is not null) variables[at] = variables[at] with { Default = v.Default };
                    if (v.Required) variables[at] = variables[at] with { Required = true };
                }
        if (Get(root, "services") is not YamlMappingNode services)
            return new ComposeFileInfo([], variables, "The file has no services: block, so there's nothing to deploy.");
        var list = new List<ComposeServiceInfo>();
        foreach (var (key, value) in Map(services))
        {
            if (key.StartsWith("x-", StringComparison.Ordinal) || value is not YamlMappingNode service) continue;
            list.Add(ReadService(key, service));
        }
        return new ComposeFileInfo(list, variables, list.Count == 0 ? "The file has no services, so there's nothing to deploy." : null);
    }

    private static ComposeServiceInfo ReadService(string name, YamlMappingNode service)
    {
        var map = Map(service);
        string? Str(string key) => map.FirstOrDefault(e => e.Key == key).Value is YamlScalarNode { Value: { Length: > 0 } v } ? v : null;
        var ports = new List<ComposePort>();
        if (map.FirstOrDefault(e => e.Key == "ports").Value is YamlSequenceNode portList)
            foreach (var item in portList.Children) ports.AddRange(Port(item));
        if (map.FirstOrDefault(e => e.Key == "expose").Value is YamlSequenceNode expose)
            foreach (var item in expose.Children.OfType<YamlScalarNode>())
                if (item.Value is { } text && int.TryParse(text.Split('/')[0].Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    ports.Add(new ComposePort(port, null, text.Contains("/udp") ? "udp" : "tcp"));
        var binds = new List<ComposeBind>();
        if (map.FirstOrDefault(e => e.Key == "volumes").Value is YamlSequenceNode volumes)
            foreach (var item in volumes.Children)
                if (Bind(item) is { } bind) binds.Add(bind);
        var copies = 1;
        if (map.FirstOrDefault(e => e.Key == "deploy").Value is YamlMappingNode deploy
            && Map(deploy).FirstOrDefault(e => e.Key == "replicas").Value is YamlScalarNode { Value: { } replicas }
            && int.TryParse(replicas, NumberStyles.None, CultureInfo.InvariantCulture, out var r)) copies = r;
        if (Str("scale") is { } scale && int.TryParse(scale, NumberStyles.None, CultureInfo.InvariantCulture, out var s)) copies = Math.Max(copies, s);
        return new ComposeServiceInfo(name, Str("image"), map.Any(e => e.Key == "build"), ports, binds, copies, Str("network_mode"));
    }

    // "8080:80", "127.0.0.1:8080:80/tcp", "80", "3000-3001:3000-3001", 8080, or the long form {target, published, host_ip, protocol}.
    private static IEnumerable<ComposePort> Port(YamlNode node)
    {
        if (node is YamlMappingNode m)
        {
            var map = Map(m);
            string? Str(string key) => map.FirstOrDefault(e => e.Key == key).Value is YamlScalarNode { Value: { Length: > 0 } v } ? v : null;
            if (int.TryParse(Str("target"), NumberStyles.None, CultureInfo.InvariantCulture, out var target))
                yield return new ComposePort(target, int.TryParse(Str("published")?.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null,
                    Str("protocol") ?? "tcp", Str("host_ip"));
            yield break;
        }
        if (node is not YamlScalarNode { Value: { Length: > 0 } text }) yield break;
        var protocol = "tcp";
        var slash = text.LastIndexOf('/');
        if (slash > 0)
        {
            protocol = text[(slash + 1)..];
            text = text[..slash];
        }
        // An IPv6 host address is in brackets: [::1]:8080:80.
        string? ip = null;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) yield break;
            ip = text[1..close];
            text = text[(close + 1)..].TrimStart(':');
        }
        var parts = text.Split(':');
        if (parts.Length == 3)
        {
            ip = parts[0];
            parts = parts[1..];
        }
        int? First(string range) => int.TryParse(range.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (parts.Length == 1 && First(parts[0]) is int only) yield return new ComposePort(only, null, protocol);
        else if (parts.Length == 2 && First(parts[1]) is int container) yield return new ComposePort(container, First(parts[0]), protocol, ip is { Length: > 0 } ? ip : null);
    }

    // A bind mount: "./conf:/etc/conf:ro" (a source that's a path, not a volume's name), or {type: bind, source, target, read_only}.
    private static ComposeBind? Bind(YamlNode node)
    {
        if (node is YamlMappingNode m)
        {
            var map = Map(m);
            string? Str(string key) => map.FirstOrDefault(e => e.Key == key).Value is YamlScalarNode { Value: { Length: > 0 } v } ? v : null;
            return Str("type") == "bind" && Str("source") is { } source && Str("target") is { } target
                ? new ComposeBind(source, target, Str("read_only") is "true" or "True" or "yes")
                : null;
        }
        if (node is not YamlScalarNode { Value: { Length: > 0 } text }) return null;
        var parts = SplitVolume(text);
        if (parts.Count < 2 || !IsPath(parts[0])) return null;
        return new ComposeBind(parts[0], parts[1], parts.Count > 2 && parts[2].Split(',').Contains("ro"));
    }

    // A short volume's parts: split on the colons outside a ${…} (whose default may hold one), a Windows drive letter (C:\x)
    // kept with its path.
    internal static List<string> SplitVolume(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (text[i] == '}' && depth > 0) depth--;
            else if (text[i] == ':' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        if (parts.Count > 2 && parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0]))
        {
            parts[1] = parts[0] + ":" + parts[1];
            parts.RemoveAt(0);
        }
        return parts;
    }

    /// <summary>Whether a volume's source is a path (a bind mount) rather than a volume's name: /x, ./x, ../x, ~/x, or a variable.</summary>
    public static bool IsPath(string source) =>
        source.StartsWith('/') || source.StartsWith('.') || source.StartsWith('~') || source.StartsWith('$') || source.Contains('/') || source.Contains('\\');

    /// <summary>
    /// The web service: the one that publishes a web port, else one that serves the web by its image or name, never a
    /// database when anything else is there; with the container port it serves on, when the file (or its image) says.
    /// </summary>
    public static ComposeWeb? PickWeb(IReadOnlyList<ComposeServiceInfo> services)
    {
        if (services.Count == 0) return null;
        var best = services.Select((s, i) => (Service: s, Score: Score(s), Index: i)).OrderByDescending(x => x.Score).ThenBy(x => x.Index).First();
        return new ComposeWeb(best.Service.Name, WebPortOf(best.Service));
    }

    /// <summary>The container port a service serves the web on: a web port it publishes, else any TCP one it publishes or exposes, else its image's.</summary>
    public static int? WebPortOf(ComposeServiceInfo s)
    {
        var tcp = s.Ports.Where(p => p.Protocol == "tcp").ToList();
        return tcp.FirstOrDefault(p => p.Published is not null && WebPorts.Contains(p.Target))?.Target
            ?? tcp.FirstOrDefault(p => p.Published is not null && !DataPorts.Contains(p.Target))?.Target
            ?? tcp.FirstOrDefault(p => WebPorts.Contains(p.Target))?.Target
            ?? tcp.FirstOrDefault(p => !DataPorts.Contains(p.Target))?.Target
            ?? (s.Image is { } image && ImagePorts.TryGetValue(Services.ContainerService.Repository(image), out var known) ? known : null);
    }

    private static int Score(ComposeServiceInfo s)
    {
        var score = 0;
        var tcp = s.Ports.Where(p => p.Protocol == "tcp").ToList();
        if (tcp.Any(p => p.Published is not null && !DataPorts.Contains(p.Target))) score += 100;
        if (tcp.Any(p => WebPorts.Contains(p.Target))) score += 50;
        if (s.Image is { } image && ImagePorts.ContainsKey(Services.ContainerService.Repository(image))) score += 30;
        if (WebNames.Contains(s.Name.ToLowerInvariant())) score += 20;
        if (s.Builds) score += 10;
        if (s.Image is { } db && Services.ContainerService.IsDatabaseImage(db) || tcp.Count > 0 && tcp.All(p => DataPorts.Contains(p.Target))) score -= 200;
        return score;
    }

    // Every text in the file: keys aren't interpolated, values are.
    private static IEnumerable<string> Scalars(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { } value }:
                yield return value;
                break;
            case YamlSequenceNode list:
                foreach (var child in list.Children)
                    foreach (var text in Scalars(child)) yield return text;
                break;
            case YamlMappingNode map:
                foreach (var (_, value) in map.Children)
                    foreach (var text in Scalars(value)) yield return text;
                break;
        }
    }

    private static YamlNode? Get(YamlMappingNode node, string key) => Map(node).FirstOrDefault(e => e.Key == key).Value;

    /// <summary>A mapping's entries in order, with merge keys (&lt;&lt;: *anchor) followed; its own keys win over merged ones.</summary>
    private static List<(string Key, YamlNode Value)> Map(YamlMappingNode node)
    {
        var own = new List<(string Key, YamlNode Value)>();
        var merged = new List<(string Key, YamlNode Value)>();
        foreach (var (k, v) in node.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? "";
            if (key == "<<")
            {
                var sources = v is YamlSequenceNode list ? list.Children.OfType<YamlMappingNode>() : v is YamlMappingNode m ? [m] : [];
                foreach (var source in sources)
                    foreach (var entry in Map(source))
                        if (merged.All(e => e.Key != entry.Key)) merged.Add(entry);
            }
            else own.Add((key, v));
        }
        return [.. own, .. merged.Where(m => own.All(o => o.Key != m.Key))];
    }
}
