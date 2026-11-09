namespace Gitberth.Deploy;

/// <summary>What decides whether a deploy can swap an app's container without its domains' visitors noticing.</summary>
/// <param name="DomainsOn">Domains are turned on in Settings, with a Cloudflare token saved.</param>
/// <param name="HasDomain">The app has a domain: without one there's nobody to keep on the old version.</param>
/// <param name="Running">The app's container runs now, so there's an old version to keep serving.</param>
/// <param name="HasVolume">The app has a volume: two copies writing the same files at once isn't safe.</param>
/// <param name="HasWebPort">The ports it's deployed with have a web port, which its domains go to.</param>
/// <param name="TunnelOnHost">cloudflared runs on the host's network, so it reaches the new version by a host port.</param>
/// <param name="WebPortInternal">The web port is Internal: it has no host port for the tunnel to reach the new version on.</param>
public sealed record BridgeFacts(bool DomainsOn, bool HasDomain, bool Running, bool HasVolume, bool HasWebPort, bool TunnelOnHost, bool WebPortInternal = false);

/// <summary>
/// Whether a deploy can start an app's new version next to the running one and move its domains over before the old one
/// stops (<see cref="DeployPipeline"/>'s bridge), so the domains' visitors don't notice. Pure: the Ports card's "domain
/// visitors aren't affected" and the pipeline read the same facts, so the card never promises a swap the deploy won't make.
/// At deploy time the pipeline can still fall back, when a temporary container left behind can't be removed or no spare
/// port is free.
/// </summary>
public static class BridgeRules
{
    /// <summary>Why the swap takes the app offline for its domains' visitors too; null when they won't notice it.</summary>
    public static string? WhyNot(BridgeFacts facts) =>
        !facts.DomainsOn || !facts.HasDomain ? "the app has no domain Gitberth routes"
        : !facts.Running ? "the app isn't running"
        : facts.HasVolume ? "the app has a volume, and two copies writing the same files at once isn't safe"
        : !facts.HasWebPort ? "the app has no web port"
        : !facts.TunnelOnHost ? "the tunnel doesn't reach apps by their host port"
        : facts.WebPortInternal ? "the web port is Internal, and the tunnel reaches apps only through a host port"
        : null;
}
