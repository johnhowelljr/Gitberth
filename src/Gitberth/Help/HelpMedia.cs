using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Help;

/// <summary>A picture of the docs: its bytes, media type and, when its file says so cheaply, its size in pixels.</summary>
public sealed record HelpMedia(string Path, byte[] Bytes, string ContentType, int? Width, int? Height)
{
    /// <summary>The media types Help serves, by extension: pictures only.</summary>
    public static string? TypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => null,
    };

    public static HelpMedia? From(string path, byte[] bytes)
    {
        if (TypeOf(path) is not { } type) return null;
        var (width, height) = SizeOf(type, bytes);
        return new HelpMedia(path, bytes, type, width, height);
    }

    /// <summary>
    /// Width and height from the file's header (WebP, PNG, GIF) or an SVG's width and height (else its viewBox), so the
    /// page keeps their room while they load. Null when the file doesn't say.
    /// </summary>
    internal static (int? Width, int? Height) SizeOf(string type, byte[] b)
    {
        try
        {
            switch (type)
            {
                case "image/png" when b.Length >= 24 && b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R':
                    return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
                case "image/gif" when b.Length >= 10:
                    return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
                case "image/webp" when b.Length >= 30 && Ascii(b, 0, "RIFF") && Ascii(b, 8, "WEBP"):
                    return WebP(b);
                case "image/svg+xml":
                    return Svg(Encoding.UTF8.GetString(b));
            }
        }
        catch (ArgumentOutOfRangeException) { }
        return (null, null);
    }

    private static bool Ascii(byte[] b, int at, string text) =>
        b.Length >= at + text.Length && Encoding.ASCII.GetString(b, at, text.Length) == text;

    // The first chunk after the header: lossy (VP8 ), lossless (VP8L) or extended (VP8X), each with the size its own way.
    private static (int?, int?) WebP(byte[] b)
    {
        if (Ascii(b, 12, "VP8X"))
            return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (Ascii(b, 12, "VP8L") && b[20] == 0x2f)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
            return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
        if (Ascii(b, 12, "VP8 ") && b[23] == 0x9d && b[24] == 0x01 && b[25] == 0x2a)
            return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3fff);
        return (null, null);
    }

    private static readonly Regex SvgTag = new(@"<svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (int?, int?) Svg(string text)
    {
        if (SvgTag.Match(text) is not { Success: true } tag) return (null, null);
        string? Attr(string name) => Regex.Match(tag.Value, $@"\s{name}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        static int? Pixels(string? v) => v is not null && Regex.Match(v.Trim(), @"^(\d+(?:\.\d+)?)(px)?$") is { Success: true } m
            ? (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
        if (Pixels(Attr("width")) is { } w && Pixels(Attr("height")) is { } h) return (w, h);
        var box = Attr("viewBox")?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (box is { Length: 4 } && double.TryParse(box[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bw)
            && double.TryParse(box[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var bh) && bw > 0 && bh > 0)
            return ((int)Math.Round(bw), (int)Math.Round(bh));
        return (null, null);
    }
}
