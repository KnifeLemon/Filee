// The preview a slicer saves inside a G-code file, for the printer's screen and for file browsers: PrusaSlicer,
// SuperSlicer, Cura, OrcaSlicer and Bambu Studio write it as base64 comment lines between "; thumbnail begin WxH LENGTH"
// and "; thumbnail end", in PNG, JPG or QOI ("thumbnail_JPG begin", "thumbnail_QOI begin"). Several sizes are common
// (a small one for the printer's file list, a large one for its preview); the largest is used.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Gcode;

internal static class GcodeThumbnail
{
    /// <summary>Largest side of a thumbnail read: slicers write up to about 1024 × 1024.</summary>
    internal const int MaxSide = 4096;

    /// <summary>Largest base64 text of one thumbnail: a 4096 × 4096 PNG of a render is far smaller.</summary>
    internal const int MaxBase64Chars = 32 * 1024 * 1024;

    /// <summary>A thumbnail: its size as the slicer wrote it, its image format (png, jpg, qoi) and its file.</summary>
    internal sealed record Image(int Width, int Height, string Format, byte[] Data);

    /// <summary>
    /// The largest thumbnail of a G-code file, or null when it has none. A thumbnail whose base64 is broken or longer
    /// than it says is skipped; one past <see cref="MaxSide"/> or <see cref="MaxBase64Chars"/> is not read.
    /// </summary>
    internal static Image? Largest(Stream stream, CancellationToken ct)
    {
        (int Width, int Height, StringBuilder Text, int Declared)? reading = null;
        Image? best = null;
        var count = 0;
        foreach (var line in GcodeToolpath.Lines(stream))
        {
            if ((++count & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            var text = line.AsSpan().TrimStart();
            if (text.Length == 0 || text[0] != ';')
            {
                reading = null; // the comment block ended without "thumbnail end"
                continue;
            }
            text = text[1..].Trim();
            if (reading is not { } open)
            {
                if (Begin(text) is { } begin && (best is null || (long)begin.Width * begin.Height > (long)best.Width * best.Height))
                    reading = (begin.Width, begin.Height, new StringBuilder(Math.Min(begin.Declared, 1 << 20)), begin.Declared);
                continue;
            }
            if (text.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) && text.EndsWith("end", StringComparison.OrdinalIgnoreCase))
            {
                reading = null;
                if (Decode(open.Text) is { } data && Sniff(data) is { } format)
                    best = new Image(open.Width, open.Height, format, data);
                continue;
            }
            if (open.Text.Length + text.Length > Math.Min(MaxBase64Chars, open.Declared + 16))
            {
                reading = null; // longer than declared: not the thumbnail the header announced
                continue;
            }
            open.Text.Append(text);
        }
        return best;
    }

    /// <summary>"thumbnail begin 220x124 7532", "thumbnail_QOI begin 480x240 9876": the size and base64 length.</summary>
    private static (int Width, int Height, int Declared)? Begin(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[1].Equals("begin", StringComparison.OrdinalIgnoreCase))
            return null;
        var size = parts[2].Split('x');
        if (parts[0].ToUpperInvariant() is not ("THUMBNAIL" or "THUMBNAIL_PNG" or "THUMBNAIL_JPG" or "THUMBNAIL_JPEG" or "THUMBNAIL_QOI")
            || size.Length != 2
            || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
            || width is < 1 or > MaxSide || height is < 1 or > MaxSide || declared is < 1 or > MaxBase64Chars)
            return null;
        return (width, height, declared);
    }

    private static byte[]? Decode(StringBuilder base64)
    {
        try
        {
            return Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The image format the data is in, by its first bytes, not by the header: Cura plug-ins and printer-firmware
    /// scripts write JPG under a plain "thumbnail begin" (PNG's). Null for anything else.
    /// </summary>
    private static string? Sniff(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? "png"
        : data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "jpg"
        : data.StartsWith("qoif"u8) ? "qoi"
        : null;
}
