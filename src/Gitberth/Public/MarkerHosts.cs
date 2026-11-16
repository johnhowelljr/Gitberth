using System.Globalization;

namespace Gitberth.Public;

/// <summary>What a marker host asks for: a domain's down page, or the status page.</summary>
public enum MarkerKind { Down, Status }

/// <param name="DomainId">The domain whose down page it is (<see cref="Data.CustomDomain.Id"/>); null for the status page.</param>
public sealed record Marker(MarkerKind Kind, int? DomainId);

/// <summary>
/// The reserved host names a tunnel rule sends to Gitberth (<c>originRequest.httpHostHeader</c>) when it should answer with
/// one of its public pages rather than its own UI. They're under <c>gitberth.invalid</c> (RFC 6761: .invalid never
/// resolves), so no real visitor or certificate can have one, and cloudflared overwrites whatever Host a visitor sent:
/// <list type="bullet">
/// <item><c>d-&lt;domainId&gt;.down.gitberth.invalid</c>: that domain's down page;</item>
/// <item><c>status.gitberth.invalid</c>: the status page.</item>
/// </list>
/// Any other name under <c>gitberth.invalid</c> is still Gitberth's, and gets a bare 404.
/// </summary>
public static class MarkerHosts
{
    /// <summary>The domain every marker host is under.</summary>
    public const string Root = "gitberth.invalid";

    /// <summary>The status page's marker host.</summary>
    public const string Status = "status." + Root;

    private const string DownSuffix = ".down." + Root;

    /// <summary>The marker host of a domain's down page.</summary>
    public static string Down(int domainId) => $"d-{domainId.ToString(CultureInfo.InvariantCulture)}{DownSuffix}";

    /// <summary>Whether a Host (with or without a port, any case, a trailing dot or not) is one of Gitberth's marker hosts.</summary>
    public static bool IsMarker(string? host) => Normalize(host) is { } h && (h == Root || h.EndsWith("." + Root, StringComparison.Ordinal));

    /// <summary>What a marker host asks for; null for any other host, and for a name under gitberth.invalid that isn't one Gitberth writes.</summary>
    public static Marker? Parse(string? host)
    {
        if (Normalize(host) is not { } h) return null;
        if (h == Status) return new Marker(MarkerKind.Status, null);
        if (h.StartsWith("d-", StringComparison.Ordinal) && h.EndsWith(DownSuffix, StringComparison.Ordinal))
        {
            var digits = h[2..^DownSuffix.Length];
            // Digits only, without a sign or leading zeros: one domain, one name.
            if (digits.Length is > 0 and <= 9 && digits.All(char.IsAsciiDigit) && (digits.Length == 1 || digits[0] != '0')
                && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                return new Marker(MarkerKind.Down, id);
        }
        return null;
    }

    /// <summary>The host name alone, lower case, without its port or a trailing dot; null when there's none.</summary>
    internal static string? Normalize(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return null;
        // An IPv6 literal has colons of its own; it's never a marker anyway.
        if (h.StartsWith('[')) return h.ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon >= 0) h = h[..colon];
        h = h.TrimEnd('.').ToLowerInvariant();
        return h.Length == 0 ? null : h;
    }
}
