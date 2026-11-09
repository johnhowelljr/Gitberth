namespace Gitberth.Components.Shared.Network;

/// <summary>The words under Settings › Own LAN addresses' Block field. Pure, so they're tested.</summary>
public static class LanBlockText
{
    /// <summary>An example block on no particular network, for when this host's network can't be read.</summary>
    public const string GenericExample = "192.168.1.224/28";

    /// <summary>
    /// The line under the Block field: the format with an example on this host's own network, what a typed block covers or
    /// what's wrong with it. When the host's network can't be read there's no example of its own, so it says so rather than
    /// leave a gap ("like , or a range").
    /// </summary>
    /// <param name="typed">What's in the field.</param>
    /// <param name="example">A block on this host's network (LanAddresses.Example); null when the network couldn't be read.</param>
    /// <param name="summary">What the typed block covers ("16 addresses, 10.1.1.224-10.1.1.239"), when it's a good one.</param>
    /// <param name="problem">Why the typed block can't be one.</param>
    public static string Help(string? typed, string? example, string? summary, string? problem)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return string.IsNullOrWhiteSpace(example)
                ? $"A /24 to a /30 of your LAN, like {GenericExample}, or a range of 4 to 256 addresses. Gitberth checks it against your network once it can read it."
                : $"A /24 to a /30, like {example}, or a range of 4 to 256 addresses.";
        if (string.IsNullOrWhiteSpace(example)) return "Gitberth can check it once it can read this host's network.";
        return summary ?? problem ?? "";
    }
}
