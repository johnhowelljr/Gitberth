using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Domains;

/// <summary>The cloudflared tunnel on this host.</summary>
/// <param name="ContainerName">The cloudflared container.</param>
/// <param name="HostNetwork">It uses the host's network, so routes go to localhost and the app's host port.</param>
/// <param name="OnAppNetwork">It's on Gitberth's network, so routes go to the app's name and container port.</param>
public sealed record TunnelInfo(string ContainerName, string AccountId, string TunnelId, bool HostNetwork, bool OnAppNetwork)
{
    /// <summary>What a domain's DNS record points at to reach this tunnel.</summary>
    public string DnsTarget => $"{TunnelId}.cfargotunnel.com";
}

/// <param name="Unreadable">cloudflared containers whose tunnel token Gitberth couldn't read (a credentials file, say).</param>
public sealed record TunnelScan(IReadOnlyList<TunnelInfo> Tunnels, IReadOnlyList<string> Unreadable);

/// <summary>Something Gitberth did or found while routing domains, for the UI and the deploy log.</summary>
public sealed record DomainNote(string? Hostname, string Text, bool Problem = false);

/// <summary>A domain Gitberth routes to its app's service.</summary>
/// <param name="Previous">Where Gitberth sent it before (the remembered service), so its rules are recognized after a port change.</param>
/// <param name="KeepPaths">Its rules keep their path filters: they're only re-pointed, never widened.</param>
public sealed record DomainRoute(string Hostname, int AppId, string Service, string? Previous = null, bool KeepPaths = false);

/// <summary>A domain whose rules to <paramref name="Service"/> (where Gitberth sent it) are to go. Rules to anything else stay.</summary>
public sealed record DomainDrop(string Hostname, string Service);

public sealed record IngressPlan(JsonArray Ingress, bool Changed, List<DomainNote> Notes);

/// <summary>
/// Finds the cloudflared containers on this host and the tunnels they run. A tunnel started with a token knows its
/// account and tunnel IDs from that token, so nothing has to be typed in. Looking never changes anything.
/// </summary>
public sealed partial class TunnelLocator(DockerCli docker)
{
    public async Task<TunnelScan> FindAllAsync(CancellationToken ct = default)
    {
        var (containers, _) = await docker.ListContainersAsync(managedOnly: false, ct);
        var tunnels = new List<TunnelInfo>();
        var unreadable = new List<string>();
        foreach (var c in containers.Where(c => c.State == "running"
                     && (c.Image.Contains("cloudflared", StringComparison.OrdinalIgnoreCase) || c.Command.EndsWith("cloudflared", StringComparison.Ordinal))))
        {
            var r = await docker.RunAsync(["container", "inspect", c.Name], null, ct);
            if (!r.Ok || Parse(r.StdOut, docker.Network) is not { } found) continue;
            var token = found.Token ?? (found.TokenFile is { } file ? await docker.ReadContainerFileAsync(found.Name, file, ct) : null);
            if (token is not null && DecodeToken(token) is var (accountId, tunnelId))
                tunnels.Add(new TunnelInfo(found.Name, accountId, tunnelId, found.HostNetwork, found.OnAppNetwork));
            else
                unreadable.Add(found.Name);
        }
        return new TunnelScan(tunnels.OrderBy(t => t.ContainerName, StringComparer.Ordinal).ToList(), unreadable);
    }

    internal sealed record CloudflaredContainer(string Name, string? Token, string? TokenFile, bool HostNetwork, bool OnAppNetwork);

    /// <summary>A cloudflared container's token (or token file) and networks, from `docker container inspect` output.</summary>
    internal static CloudflaredContainer? Parse(string inspectJson, string network)
    {
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);
            var c = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            var args = new List<string>();
            var env = new List<string>();
            Collect(c, "Args", args);
            if (c.TryGetProperty("Config", out var config))
            {
                Collect(config, "Entrypoint", args);
                Collect(config, "Cmd", args);
                Collect(config, "Env", env);
            }
            var token = Option(args, "--token") ?? Variable(env, "TUNNEL_TOKEN");
            var tokenFile = Option(args, "--token-file") ?? Variable(env, "TUNNEL_TOKEN_FILE");
            var name = c.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.TrimStart('/') : "";
            var hostNetwork = c.TryGetProperty("HostConfig", out var host) && host.TryGetProperty("NetworkMode", out var mode) && mode.GetString() == "host";
            var onAppNetwork = c.TryGetProperty("NetworkSettings", out var settings) && settings.TryGetProperty("Networks", out var networks)
                && networks.ValueKind == JsonValueKind.Object && networks.TryGetProperty(network, out _);
            return new CloudflaredContainer(name, token, tokenFile, hostNetwork, onAppNetwork);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A tunnel token is base64 JSON: {"a": account ID, "t": tunnel ID, "s": secret}. Only the two IDs are kept.</summary>
    internal static (string AccountId, string TunnelId)? DecodeToken(string token)
    {
        var s = token.Trim().Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            var account = doc.RootElement.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var tunnel = doc.RootElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            // Both go into API paths, so only well-formed IDs are accepted.
            return account is not null && AccountId().IsMatch(account) && Guid.TryParse(tunnel, out var id)
                ? (account, id.ToString("D"))
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Option(List<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name && i + 1 < args.Count) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    private static string? Variable(List<string> env, string name) =>
        env.FirstOrDefault(e => e.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    private static void Collect(JsonElement parent, string name, List<string> into)
    {
        if (parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            into.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex AccountId();
}

/// <summary>
/// The tunnel's ingress rules, as Gitberth manages them. A domain's rules are Gitberth's when they go where Gitberth sends it
/// now or sent it before (the remembered service); everyone else's rules keep their place and their settings. A domain
/// gets one rule for every path, written where its rules were (a new domain goes right after the rules that apply to every
/// host), unless it keeps its path filters, in which case its rules are only re-pointed. The catch-all Cloudflare requires
/// stays last.
/// </summary>
public static class TunnelRoutes
{
    /// <summary>
    /// Where the tunnel sends an app's domains: its web port on the running container, as cloudflared reaches it. On the
    /// host's network that's the container's binding of the web port (http://localhost:3001, or http://127.0.0.1:3002 for a
    /// host-only one); on the shared network it's the app's network name and the web port, published or not
    /// (http://kanban:3000). Null when the app has no web port or isn't running, when its container doesn't have the web
    /// port that way yet (a saved change not applied), or when cloudflared can't reach it: nothing moves before the
    /// container does.
    /// </summary>
    /// <param name="ports">What docker ps shows in the running container's Ports column ("0.0.0.0:3001->3000/tcp, 9229/tcp"); null when it isn't running.</param>
    public static string? ServiceFor(AppDefinition app, string? ports, TunnelInfo tunnel)
    {
        if (ports is null || PortRules.WebOf(app.Ports) is not { } web) return null;
        if (tunnel.HostNetwork)
            return DockerCli.ParsePortBindings(ports).FirstOrDefault(p => p.Protocol == "tcp" && p.ContainerPort == web.ContainerPort) is { } binding
                ? $"http://{Address(binding.HostIp)}:{binding.HostPort}"
                : null;
        // By the name it answers to on the shared network: an app's slug, an Existing app's container name.
        return tunnel.OnAppNetwork && DockerCli.ParseContainerPorts(ports).Contains(new ExposedPort(web.ContainerPort, "tcp"))
            ? $"http://{app.NetworkName}:{web.ContainerPort}"
            : null;
    }

    /// <summary>
    /// Where a working route goes, in the Domains card's words: "Port 3000, through host port 3001", "(host only)" after a
    /// host-only binding, "Port 3000, by name (kanban:3000)" when cloudflared is on the shared network, and "Port 3000 on the
    /// new version, through host port 8100" while a deploy runs the new version next to the old one.
    /// </summary>
    /// <param name="service">Where the route goes, as <see cref="ServiceFor"/> gave it.</param>
    /// <param name="newVersion">It goes to a deploy's new version rather than to the app's own container.</param>
    public static string Describe(AppDefinition app, string service, bool newVersion = false)
    {
        if (!Uri.TryCreate(service, UriKind.Absolute, out var uri)) return $"Routed to {service}";
        var on = newVersion ? " on the new version" : "";
        if (HostPortOf(service) is not int hostPort) return $"Port {uri.Port}{on}, by name ({uri.Host}:{uri.Port})";
        // ServiceFor writes a binding on every address as localhost, and a host-only one as its loopback address.
        var hostOnly = !newVersion && uri.Host.Trim('[', ']') is "127.0.0.1" or "::1";
        return PortRules.WebOf(app.Ports) is { } web
            ? $"Port {web.ContainerPort}{on}, through host port {hostPort}{(hostOnly ? " (host only)" : "")}"
            : $"Host port {hostPort}";
    }

    /// <summary>
    /// Whether a rule's service goes to the same place as <paramref name="target"/>: the same scheme, port and host, counting
    /// localhost, 127.0.0.1 and ::1 as one, and an app's name and container name as one.
    /// </summary>
    public static bool SameService(string? service, string? target, AppDefinition? app = null)
    {
        if (!Uri.TryCreate(service, UriKind.Absolute, out var a) || !Uri.TryCreate(target, UriKind.Absolute, out var b)) return false;
        if (a.Scheme != b.Scheme || a.Port != b.Port) return false;
        var (x, y) = (a.Host.Trim('[', ']'), b.Host.Trim('[', ']'));
        if (x == y || (IsLoopback(x) && IsLoopback(y))) return true;
        return app is not null && (x == app.NetworkName || x == app.ContainerName) && (y == app.NetworkName || y == app.ContainerName);
    }

    /// <summary>
    /// Domains made in Cloudflare's dashboard that Gitberth takes over: every rule for the host already goes exactly where
    /// Gitberth would send it (<paramref name="services"/>, by app; plain http). One whose rules all carry path filters is
    /// taken over as it is (KeepPaths) and never widened until someone chooses to serve every path. A host behind a
    /// wildcard rule, split between services, or sent to another port or over https stays as it was made.
    /// </summary>
    public static List<(string Hostname, int AppId, bool KeepPaths, string Service)> FindAdoptable(
        JsonArray ingress, IReadOnlyCollection<AppDefinition> apps, IReadOnlyDictionary<int, string> services, IReadOnlySet<string> skip)
    {
        var rules = Rules(ingress).ToList();
        var found = new List<(string, int, bool, string)>();
        foreach (var group in rules.Where(r => Host(r) is not null).GroupBy(r => Host(r)!.ToLowerInvariant()))
        {
            if (skip.Contains(group.Key) || DomainNames.Problem(group.Key) is not null || ShadowedByWildcard(rules, group.Key)) continue;
            var owners = services
                .Where(s => apps.FirstOrDefault(a => a.Id == s.Key) is { } app && group.All(r => SameService(Service(r), s.Value, app)))
                .Select(s => s.Key)
                .ToList();
            if (owners is [var appId]) found.Add((group.Key, appId, !group.Any(r => Path(r) is null), Service(group.First())!));
        }
        return found;
    }

    /// <summary>
    /// The ingress with Gitberth's domains routed and its removed ones gone. Rules that are already right are left exactly as
    /// they are, so a sync with nothing to do changes nothing. A dropped domain loses only its rules to where Gitberth sent it.
    /// </summary>
    public static IngressPlan Plan(JsonArray current, IReadOnlyCollection<AppDefinition> apps, IReadOnlyList<DomainRoute> routes, IReadOnlyList<DomainDrop> drops)
    {
        var rules = Rules(current).ToList();
        var catchAll = rules.Count > 0 && IsCatchAll(rules[^1]) ? rules[^1] : null;
        var body = rules.Where(r => r != catchAll).ToList();
        var byHost = routes.ToDictionary(r => r.Hostname, StringComparer.OrdinalIgnoreCase);
        var dropped = drops.GroupBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Service).ToList(), StringComparer.OrdinalIgnoreCase);
        var notes = new List<DomainNote>();

        AppDefinition? AppOf(DomainRoute route) => apps.FirstOrDefault(a => a.Id == route.AppId);
        // Gitberth's rules for the domain: they go where it sends the domain now or sent it before.
        bool Mine(JsonObject rule, DomainRoute route) =>
            SameService(Service(rule), route.Service, AppOf(route)) || SameService(Service(rule), route.Previous, AppOf(route));

        var output = new List<JsonNode?>();
        // Rules for every host at the top apply first, to every host; they stay first.
        var leading = body.TakeWhile(r => Host(r) is null).ToList();
        output.AddRange(leading.Select(r => r.DeepClone()));
        // New domains, and ones a wildcard above would catch first, go right after them.
        var placedFirst = routes
            .Where(r => !r.KeepPaths && (!body.Any(x => SameHost(x, r.Hostname)) || ShadowedByWildcard(body, r.Hostname)))
            .OrderBy(r => r.Hostname, StringComparer.Ordinal)
            .ToList();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in placedFirst)
        {
            if (body.Any(x => SameHost(x, route.Hostname)))
                notes.Add(new DomainNote(route.Hostname, $"Moved {route.Hostname} above the wildcard rule that was catching its traffic first"));
            output.AddRange(Write(route));
        }

        foreach (var rule in body.Skip(leading.Count))
        {
            var host = Host(rule);
            if (host is not null && byHost.TryGetValue(host, out var route))
            {
                if (route.KeepPaths)
                {
                    // Re-pointed in place, path, settings and all; never widened.
                    output.Add(Mine(rule, route) ? Repoint(rule, route) : rule.DeepClone());
                    continue;
                }
                if (written.Contains(route.Hostname)) continue;
                output.AddRange(Write(route));
                continue;
            }
            if (host is not null && dropped.TryGetValue(host, out var services) && services.Any(s => SameService(Service(rule), s)))
            {
                notes.Add(new DomainNote(host.ToLowerInvariant(), $"Removed the tunnel's route for {host}{(Path(rule) is { } p ? $" (path \"{p}\")" : "")}"));
                continue;
            }
            output.Add(rule.DeepClone());
        }
        output.Add(catchAll?.DeepClone() ?? new JsonObject { ["service"] = "http_status:404" });
        var ingress = new JsonArray([.. output]);
        return new IngressPlan(ingress, !JsonNode.DeepEquals(current, ingress), notes);

        // The domain's rules to anything else, then one rule for every path, unless its own rules already send every path there.
        IEnumerable<JsonNode> Write(DomainRoute route)
        {
            written.Add(route.Hostname);
            var old = body.Where(x => SameHost(x, route.Hostname)).ToList();
            var mine = old.Where(x => Mine(x, route)).ToList();
            var app = AppOf(route);
            if (mine.Count > 0 && mine.All(x => SameService(Service(x), route.Service, app)) && mine.Any(x => Path(x) is null))
                return old.Select(x => x.DeepClone()).ToList();
            // It serves every path already: its own rules are only re-pointed, path, settings and all (a path rule's Access
            // included), so a domain that moves to a deploy's temporary container and back comes back the same. Other
            // services' path rules still go ahead of its every-path rule. Only a domain with no every-path rule of its own
            // is written as one, when it's widened or taken over.
            if (mine.FirstOrDefault(x => Path(x) is null) is { } every)
            {
                var at = old.IndexOf(every);
                var ahead = old.Take(at).Where(x => Mine(x, route) || Path(x) is not null)
                    .Concat(old.Skip(at + 1).Where(x => !Mine(x, route) && Path(x) is not null));
                return [.. ahead.Select(x => Mine(x, route) ? Repoint(x, route) : x.DeepClone()), .. old.Skip(at).Where(x => Mine(x, route)).Select(x => Repoint(x, route))];
            }
            var foreign = old.Where(x => !Mine(x, route) && Path(x) is not null).ToList();
            var rule = new JsonObject { ["hostname"] = route.Hostname, ["service"] = route.Service };
            // Settings carry over, from its every-path rule or else from the filtered one being widened (Access included).
            var source = mine.FirstOrDefault(x => Path(x) is null) ?? mine.FirstOrDefault();
            if (source?["originRequest"] is JsonObject origin) rule["originRequest"] = origin.DeepClone();
            if (mine.Count == 0 && old.Count == 0) notes.Add(new DomainNote(route.Hostname, $"Routed {route.Hostname} to {route.Service}"));
            else if (mine.Count == 0) notes.Add(new DomainNote(route.Hostname, $"Pointed {route.Hostname} at {route.Service}, instead of {Service(old.FirstOrDefault(x => Path(x) is null) ?? old[0])}"));
            else if (mine.FirstOrDefault(x => Path(x) is not null) is { } filtered && !mine.Any(x => Path(x) is null))
                notes.Add(new DomainNote(route.Hostname, $"{route.Hostname} now sends every path to the app. Its path filter \"{Path(filtered)}\" let only matching addresses through, so Cloudflare answered 404 for the app's other pages, scripts and sign-in."));
            else notes.Add(new DomainNote(route.Hostname, $"Pointed {route.Hostname} at {route.Service}, instead of {Service(mine[0])}"));
            return [.. foreign.Select(f => f.DeepClone()), rule];
        }

        JsonNode Repoint(JsonObject rule, DomainRoute route)
        {
            var copy = (JsonObject)rule.DeepClone();
            if (!SameService(Service(rule), route.Service, AppOf(route)))
            {
                copy["service"] = route.Service;
                notes.Add(new DomainNote(route.Hostname, $"Pointed {route.Hostname}{(Path(rule) is { } p ? $" (path \"{p}\")" : "")} at {route.Service}, instead of {Service(rule)}"));
            }
            return copy;
        }
    }

    /// <summary>
    /// Whether a domain's rules send it to the app, and what would fix it when they don't. Rules are read in the tunnel's
    /// order: the first one with no path decides where everything outside the path rules above it goes.
    /// </summary>
    /// <param name="newVersion">The app's domains go to a deploy's new version for now (see <see cref="Describe"/>).</param>
    public static (bool Ok, string Summary, DomainFix Fix) RouteState(JsonArray ingress, string hostname, AppDefinition app, string service, string? previous, bool keepPaths,
        bool newVersion = false)
    {
        var all = Rules(ingress).ToList();
        if (ShadowedByWildcard(all, hostname))
            return (false, "A wildcard rule above it in the tunnel catches its traffic first.", keepPaths ? DomainFix.None : DomainFix.Sync);
        var rules = all.Where(r => SameHost(r, hostname)).ToList();
        // One kept on its path filters whose rules were deleted in Cloudflare only comes back for every path, when asked.
        if (rules.Count == 0) return (false, "The tunnel has no route for it yet.", keepPaths ? DomainFix.ServeAllPaths : DomainFix.Sync);
        bool Current(JsonObject r) => SameService(Service(r), service, app);
        bool Before(JsonObject r) => SameService(Service(r), previous, app);
        if (rules.Any(r => !Current(r) && Before(r)))
            return (false, $"The tunnel still sends it to {previous}, where the app was before.", DomainFix.Sync);
        var decider = rules.FirstOrDefault(r => Path(r) is null);
        if (decider is not null && Current(decider)) return (true, Describe(app, service, newVersion), DomainFix.None);
        // Widening always takes a click, whether Gitberth took it over that way or someone narrowed it in Cloudflare since.
        if (decider is null && rules.FirstOrDefault(r => Current(r)) is { } filtered)
            return (false, $"Only addresses matching \"{Path(filtered)}\" reach the app, so Cloudflare answers 404 for its other pages, scripts and sign-in.", DomainFix.ServeAllPaths);
        var other = decider ?? rules[0];
        return (false, $"The tunnel sends it to {Service(other)}, not to this app at {service}.", keepPaths ? DomainFix.None : DomainFix.Sync);
    }

    /// <summary>
    /// Whether a domain Gitberth routes for every path has been narrowed in Cloudflare's dashboard: its rules to the app all
    /// carry path filters now. Gitberth keeps it that way rather than widening it again without asking.
    /// </summary>
    public static bool Narrowed(JsonArray ingress, string hostname, AppDefinition app, string service, string? previous)
    {
        var mine = Rules(ingress).Where(r => SameHost(r, hostname) && (SameService(Service(r), service, app) || SameService(Service(r), previous, app))).ToList();
        return mine.Count > 0 && mine.All(r => Path(r) is not null);
    }

    /// <summary>Whether the tunnel still has any rule for the host name.</summary>
    public static bool HasRules(JsonArray ingress, string hostname) => Rules(ingress).Any(r => SameHost(r, hostname));

    /// <summary>The host ports on this machine that any rule goes to.</summary>
    public static HashSet<int> HostPorts(JsonArray ingress) => Rules(ingress).Select(r => HostPortOf(Service(r))).OfType<int>().ToHashSet();

    /// <summary>
    /// The service text the tunnel actually has for the domain's rule to the app (it may name the container where Gitberth
    /// would use the app's name). Remembering that exact text lets a later removal find the rule after the app is gone.
    /// </summary>
    public static string? ServiceOf(JsonArray ingress, string hostname, string service, AppDefinition app) =>
        Rules(ingress).Where(r => SameHost(r, hostname)).Select(Service).FirstOrDefault(s => SameService(s, service, app));

    /// <summary>The host port a service on this machine uses (http://localhost:3001 gives 3001); null for one that names an app.</summary>
    public static int? HostPortOf(string? service) =>
        Uri.TryCreate(service, UriKind.Absolute, out var uri) && uri.Port > 0
        && (IsLoopback(uri.Host.Trim('[', ']')) || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            ? uri.Port
            : null;

    // ---- GitHub's webhook: one path of one host name goes to Gitberth itself ----

    /// <summary>The path rule GitHub's webhook deliveries take to Gitberth: exactly /hooks/github, nothing else.</summary>
    public const string WebhookPath = "^/hooks/github$";

    /// <summary>What the tunnel answers for every other address of the webhook's host name.</summary>
    public const string NotFoundService = "http_status:404";

    /// <summary>
    /// The ingress with GitHub's webhook routed: on <paramref name="host"/> only the path /hooks/github goes to Gitberth
    /// (<paramref name="service"/>), and every other path gets a 404 of its own (unless someone else has rules on that host,
    /// which then stay theirs). Never behind Access: the rule has no originRequest. The webhook's rules on the hosts in
    /// <paramref name="drop"/> (a host it moved away from) go. With no service (Gitberth can't be reached from the tunnel)
    /// the host's rules stay as they are.
    /// </summary>
    public static IngressPlan PlanWebhook(JsonArray current, string? host, string? service, IReadOnlyCollection<string> drop)
    {
        var rules = Rules(current).ToList();
        var notes = new List<DomainNote>();
        var output = new List<JsonObject>();
        var gone = drop.Where(d => !string.Equals(d, host, StringComparison.OrdinalIgnoreCase) && rules.Any(r => IsWebhookRule(r, d))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (Host(rule) is { } h && gone.Contains(h) && (IsWebhookRule(rule, h) || IsNotFoundRule(rule, h))) continue;
            output.Add(rule);
        }
        foreach (var h in gone.Order(StringComparer.Ordinal)) notes.Add(new DomainNote(h.ToLowerInvariant(), $"Removed the route GitHub's webhook took through {h}"));

        if (host is not null && service is not null)
        {
            var own = output.Where(r => SameHost(r, host)).ToList();
            var foreign = own.Where(r => !IsWebhookRule(r, host) && !IsNotFoundRule(r, host)).ToList();
            var wanted = new List<JsonObject> { new() { ["hostname"] = host, ["path"] = WebhookPath, ["service"] = service } };
            if (foreign.Count == 0) wanted.Add(new JsonObject { ["hostname"] = host, ["service"] = NotFoundService });
            var mine = own.Except(foreign).ToList();
            var inPlace = mine.Count == wanted.Count && mine.Zip(wanted).All(p => JsonNode.DeepEquals(p.First, p.Second))
                && output.IndexOf(mine[0]) + mine.Count - 1 == output.IndexOf(mine[^1]) && !ShadowedByWildcard(output, host);
            if (!inPlace)
            {
                output.RemoveAll(mine.Contains);
                // Right after the rules for every host, like a new domain: ahead of any wildcard that would catch it.
                var at = output.TakeWhile(r => Host(r) is null && !IsCatchAll(r)).Count();
                output.InsertRange(at, wanted);
                notes.Add(new DomainNote(host, $"Routed https://{host}{GitHubWebhookPathText} to Gitberth for GitHub's webhooks"));
            }
        }
        if (output.Count == 0 || !IsCatchAll(output[^1])) output.Add(new JsonObject { ["service"] = NotFoundService });
        var ingress = new JsonArray([.. output.Select(r => (JsonNode?)r.DeepClone())]);
        return new IngressPlan(ingress, !JsonNode.DeepEquals(current, ingress), notes);
    }

    private const string GitHubWebhookPathText = "/hooks/github";

    private static bool IsWebhookRule(JsonObject rule, string host) => SameHost(rule, host) && Path(rule) == WebhookPath;

    private static bool IsNotFoundRule(JsonObject rule, string host) => SameHost(rule, host) && Path(rule) is null && Service(rule) == NotFoundService;

    /// <summary>Whether the host has rules that aren't the webhook's: then it's someone else's, and can't be the webhook's host.</summary>
    public static bool UsedByOthers(JsonArray ingress, string host) =>
        Rules(ingress).Any(r => SameHost(r, host) && !IsWebhookRule(r, host) && !IsNotFoundRule(r, host));

    // ---- Cloudflare Access on a domain's rules ----

    /// <summary>What a rule's originRequest.access holds for cloudflared to check the visitor's Access sign-in.</summary>
    public static JsonObject AccessBlock(string teamName, string aud) => new()
    {
        ["required"] = true,
        ["teamName"] = teamName,
        ["audTag"] = new JsonArray(aud),
    };

    /// <summary>A protected domain: the access block its rules get, and the services that make a rule for it Gitberth's.</summary>
    public sealed record Protection(JsonObject Access, IReadOnlyList<string> Services);

    /// <summary>
    /// The ingress with every protected domain's own rules (the ones going to the app's service, now or before) carrying
    /// its access block, and the access blocks of Gitberth's removed Access applications (by audience tag) taken off any
    /// rule. Everything else about each rule stays.
    /// </summary>
    public static IngressPlan ApplyAccess(JsonArray current, IReadOnlyDictionary<string, Protection> protect, IReadOnlySet<string> stripAuds, IReadOnlyCollection<AppDefinition> apps)
    {
        var notes = new List<DomainNote>();
        var output = new List<JsonNode?>();
        foreach (var rule in Rules(current))
        {
            var copy = (JsonObject)rule.DeepClone();
            var host = Host(rule);
            if (host is not null && protect.TryGetValue(host, out var p) && p.Services.Any(s => apps.Any(a => SameService(Service(rule), s, a)) || SameService(Service(rule), s)))
            {
                var origin = copy["originRequest"] as JsonObject ?? [];
                if (!JsonNode.DeepEquals(origin["access"], p.Access))
                {
                    origin["access"] = p.Access.DeepClone();
                    copy["originRequest"] = origin.DeepClone();
                    if (!notes.Any(n => n.Hostname == host.ToLowerInvariant()))
                        notes.Add(new DomainNote(host.ToLowerInvariant(), $"{host} is behind Cloudflare Access now: visitors sign in first"));
                }
            }
            else if (copy["originRequest"] is JsonObject origin && origin["access"] is JsonObject access
                     && (access["audTag"] as JsonArray ?? []).Any(t => t is JsonValue v && v.TryGetValue<string>(out var aud) && stripAuds.Contains(aud)))
            {
                origin.Remove("access");
                if (origin.Count == 0) copy.Remove("originRequest");
                if (host is not null && !notes.Any(n => n.Hostname == host.ToLowerInvariant()))
                    notes.Add(new DomainNote(host.ToLowerInvariant(), $"{host} isn't behind Cloudflare Access any more"));
            }
            output.Add(copy);
        }
        var ingress = new JsonArray([.. output]);
        return new IngressPlan(ingress, !JsonNode.DeepEquals(current, ingress), notes);
    }

    /// <summary>Whether every rule for the host makes cloudflared check an Access sign-in.</summary>
    public static bool BehindAccess(JsonArray ingress, string hostname)
    {
        var rules = Rules(ingress).Where(r => SameHost(r, hostname)).ToList();
        return rules.Count > 0 && rules.All(r => r["originRequest"] is JsonObject o && o["access"] is JsonObject a && a["required"] is JsonValue v && v.TryGetValue<bool>(out var on) && on);
    }

    /// <summary>Whether a wildcard rule (*.example.com) covering the host comes before the host's own first rule.</summary>
    private static bool ShadowedByWildcard(List<JsonObject> rules, string hostname)
    {
        foreach (var rule in rules)
        {
            if (SameHost(rule, hostname)) return false;
            if (Text(rule, "hostname") is { } h && h.StartsWith("*.", StringComparison.Ordinal)
                && hostname.EndsWith(h[1..], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsCatchAll(JsonObject rule) => Host(rule) is null && Path(rule) is null;

    private static bool IsLoopback(string host) => host is "localhost" or "127.0.0.1" or "::1";

    private static string Address(string? hostIp) =>
        string.IsNullOrWhiteSpace(hostIp) || hostIp is "0.0.0.0" or "::" ? "localhost" : hostIp.Contains(':') ? $"[{hostIp}]" : hostIp;

    private static IEnumerable<JsonObject> Rules(JsonArray ingress) => ingress.OfType<JsonObject>();

    private static bool SameHost(JsonObject rule, string hostname) => string.Equals(Host(rule), hostname, StringComparison.OrdinalIgnoreCase);

    // "*" alone matches every host, so it counts as no host name.
    private static string? Host(JsonObject rule) => Text(rule, "hostname") is { Length: > 0 } h && h != "*" ? h : null;

    private static string? Path(JsonObject rule) => Text(rule, "path") is { Length: > 0 } p ? p : null;

    private static string? Service(JsonObject rule) => Text(rule, "service");

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>Domain names as people type them: a URL or a bare name, any case.</summary>
public static partial class DomainNames
{
    /// <summary>The lowercase host name in what was typed (https://App.Example.com/path works too), or null.</summary>
    public static string? Normalize(string? input)
    {
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost.TrimEnd('.').ToLowerInvariant()
            : null;
    }

    /// <summary>Why a host name can't be routed, or null when it can.</summary>
    public static string? Problem(string? hostname) =>
        hostname is null ? "Enter a domain, like app.example.com."
        : !HostName().IsMatch(hostname) ? $"\"{hostname}\" isn't a domain Gitberth can route. Use a name like app.example.com."
        : null;

    /// <summary>
    /// Cloudflare's free certificate covers a zone and one level below it (example.com, *.example.com). A name further
    /// down, like a.b.example.com, needs Total TLS or an advanced certificate before browsers accept it.
    /// </summary>
    public static string? CertificateWarning(string hostname, string zone) =>
        hostname.EndsWith("." + zone, StringComparison.Ordinal) && hostname[..^(zone.Length + 1)].Contains('.')
            ? $"Cloudflare's free certificate covers {zone} and *.{zone} only, so {hostname} needs Total TLS or an advanced certificate in Cloudflare."
            : null;

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostName();
}
