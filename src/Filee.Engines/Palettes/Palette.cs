// Colour palettes (#1615): the .pal layouts that are documented (a raw 768-byte palette as DOS and retro games keep it,
// Paint Shop Pro's JASC-PAL text, Microsoft's RIFF PAL), GIMP's .gpl and Adobe's .act, read into one list of colours
// and written as GPL, ACT, a list of HEX codes (TXT, JSON) or a swatch drawn as SVG.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;

namespace Filee.Engines.Palettes;

/// <summary>One colour of a palette, with the name a GIMP palette may give it.</summary>
internal readonly record struct PaletteColor(byte R, byte G, byte B, string? Name = null)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>A palette's colours in order.</summary>
internal sealed record Palette(IReadOnlyList<PaletteColor> Colors, string? Name = null)
{
    /// <summary>More than any palette format here holds; a larger file is not a palette.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>GPL and RIFF state their own count; nothing reasonable has more colours than this.</summary>
    public const int MaxColors = 65536;

    /// <summary>
    /// Reads <paramref name="path"/> of format <paramref name="format"/> ("pal", "gpl", "act"). A .pal in a layout
    /// that isn't documented is refused as unsupported rather than guessed at.
    /// </summary>
    public static Palette Read(string path, string format)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new ConversionRefusedException("error.unsupported_source", "larger than any palette");
        var bytes = File.ReadAllBytes(path);
        return format switch
        {
            "gpl" => ReadGpl(bytes),
            "act" => ReadAct(bytes),
            _ => ReadPal(bytes),
        };
    }

    /// <summary>RIFF PAL, JASC-PAL or a raw 768-byte palette, told apart by their signatures and size.</summary>
    internal static Palette ReadPal(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("PAL "u8))
            return ReadRiff(bytes);
        // A JASC file saved by a text editor may start with a UTF-8 byte order mark.
        if (bytes.AsSpan().StartsWith("JASC-PAL"u8) || bytes.AsSpan().StartsWith("\uFEFFJASC-PAL"u8))
            return ReadJasc(bytes);
        if (bytes.Length == 768)
            return ReadRaw(bytes);
        throw new ConversionRefusedException("error.unsupported_source", "not a RIFF, JASC or 768-byte palette");
    }

    /// <summary>
    /// 256 RGB triplets. VGA palettes store 6 bits per channel (0–63): when no value is above 63 they are scaled up
    /// to 0–255, as the hardware showed them.
    /// </summary>
    private static Palette ReadRaw(byte[] bytes)
    {
        var sixBit = bytes.All(b => b <= 63);
        byte Channel(byte value) => sixBit ? (byte)((value * 255 + 31) / 63) : value;
        var colors = new PaletteColor[256];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = new PaletteColor(Channel(bytes[i * 3]), Channel(bytes[i * 3 + 1]), Channel(bytes[i * 3 + 2]));
        return new Palette(colors);
    }

    /// <summary>
    /// "RIFF" size "PAL ", then chunks; the "data" chunk is a LOGPALETTE: version 0x0300, a count, and four bytes per
    /// colour (red, green, blue, flags).
    /// </summary>
    private static Palette ReadRiff(byte[] bytes)
    {
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var body = position + 8;
            if (size > (uint)(bytes.Length - body))
                break;
            if (id.SequenceEqual("data"u8) && size >= 4)
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                if (count == 0 || 4 + count * 4L > size)
                    break;
                var colors = new PaletteColor[count];
                for (var i = 0; i < count; i++)
                {
                    var entry = body + 4 + i * 4;
                    colors[i] = new PaletteColor(bytes[entry], bytes[entry + 1], bytes[entry + 2]);
                }
                return new Palette(colors);
            }
            // Chunks are padded to an even size.
            position = body + (int)size + (int)(size & 1);
        }
        throw new ConversionRefusedException("error.unsupported_source", "RIFF palette without colours");
    }

    /// <summary>"JASC-PAL", "0100", the count, then one "R G B" line per colour.</summary>
    private static Palette ReadJasc(byte[] bytes)
    {
        var lines = Lines(bytes);
        if (lines.Count < 3 || !int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count is <= 0 or > MaxColors || lines.Count < 3 + count)
            throw new ConversionRefusedException("error.unsupported_source", "damaged JASC palette");
        var colors = new List<PaletteColor>(count);
        foreach (var line in lines.Skip(3).Take(count))
            colors.Add(ParseRgb(line, out _) ?? throw new ConversionRefusedException("error.unsupported_source", "damaged JASC palette"));
        return new Palette(colors);
    }

    /// <summary>
    /// "GIMP Palette", optional "Name:" and "Columns:" lines, "#" comments, then "R G B name" lines (the name is
    /// optional and may contain spaces).
    /// </summary>
    internal static Palette ReadGpl(byte[] bytes)
    {
        var lines = Lines(bytes);
        if (lines.Count == 0 || !lines[0].StartsWith("GIMP Palette", StringComparison.Ordinal))
            throw new ConversionRefusedException("error.unsupported_source", "not a GIMP palette");
        string? name = null;
        var colors = new List<PaletteColor>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                name = line[5..].Trim() is { Length: > 0 } n ? n : null;
                continue;
            }
            if (line.StartsWith("Columns:", StringComparison.Ordinal))
                continue;
            if (ParseRgb(line, out var colorName) is not { } color)
                throw new ConversionRefusedException("error.unsupported_source", "damaged GIMP palette");
            if (colors.Count == MaxColors)
                break;
            colors.Add(color with { Name = colorName });
        }
        if (colors.Count == 0)
            throw new ConversionRefusedException("error.unsupported_source", "GIMP palette without colours");
        return new Palette(colors, name);
    }

    /// <summary>
    /// 256 RGB triplets, optionally followed by the number of colours used and the transparent index (big-endian,
    /// Photoshop CS2 and later). A count of 0 or above 256 means all 256.
    /// </summary>
    internal static Palette ReadAct(byte[] bytes)
    {
        if (bytes.Length is not (768 or 772))
            throw new ConversionRefusedException("error.unsupported_source", "not a 768- or 772-byte color table");
        var count = 256;
        if (bytes.Length == 772 && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(768, 2)) is var used and > 0 and <= 256)
            count = used;
        var colors = new PaletteColor[count];
        for (var i = 0; i < count; i++)
            colors[i] = new PaletteColor(bytes[i * 3], bytes[i * 3 + 1], bytes[i * 3 + 2]);
        return new Palette(colors);
    }

    /// <summary>"R G B" (whitespace between, each 0–255), and whatever follows as the name.</summary>
    private static PaletteColor? ParseRgb(string line, out string? name)
    {
        name = null;
        var parts = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;
        Span<byte> rgb = stackalloc byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out rgb[i]))
                return null;
        }
        name = parts.Length == 4 ? parts[3].Trim() : null;
        return new PaletteColor(rgb[0], rgb[1], rgb[2]);
    }

    private static List<string> Lines(byte[] bytes) =>
        [.. Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n').Select(line => line.TrimEnd('\r').Trim())];

    // ───────── Writing ─────────

    /// <summary>A GIMP palette with 16 columns, the colours' names kept.</summary>
    public string ToGpl(string fallbackName)
    {
        var sb = new StringBuilder();
        sb.Append("GIMP Palette\n");
        sb.Append("Name: ").Append(OneLine(Name ?? fallbackName)).Append('\n');
        sb.Append("Columns: 16\n#\n");
        foreach (var color in Colors)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{color.R,3} {color.G,3} {color.B,3}\t");
            sb.Append(color.Name is { Length: > 0 } n ? OneLine(n) : color.Hex).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// An Adobe color table: 768 bytes of RGB, then the number of colours used and no transparent index (0xFFFF).
    /// Colours past 256 don't fit and are left out.
    /// </summary>
    public byte[] ToAct()
    {
        var bytes = new byte[772];
        var count = Math.Min(Colors.Count, 256);
        for (var i = 0; i < count; i++)
        {
            bytes[i * 3] = Colors[i].R;
            bytes[i * 3 + 1] = Colors[i].G;
            bytes[i * 3 + 2] = Colors[i].B;
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(768), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(770), 0xFFFF);
        return bytes;
    }

    /// <summary>One "#RRGGBB" per line.</summary>
    public string ToHexList() => string.Concat(Colors.Select(c => c.Hex + "\n"));

    /// <summary>The palette's name and, per colour, its HEX code, red, green, blue and name.</summary>
    public string ToJson(string fallbackName)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", Name ?? fallbackName);
            json.WriteStartArray("colors");
            foreach (var color in Colors)
            {
                json.WriteStartObject();
                json.WriteString("hex", color.Hex);
                json.WriteNumber("r", color.R);
                json.WriteNumber("g", color.G);
                json.WriteNumber("b", color.B);
                if (color.Name is { Length: > 0 } n)
                    json.WriteString("name", n);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Size of a swatch in the picture, in pixels.</summary>
    internal const int Swatch = 32;

    /// <summary>
    /// A swatch picture: the colours as squares in rows of 16 (fewer for a small palette), so PNG, PDF and the other
    /// pictures come from SVG like an embroidery design's.
    /// </summary>
    public string ToSvg(string fallbackName)
    {
        var columns = Math.Clamp(Colors.Count, 1, 16);
        var rows = Math.Max(1, (Colors.Count + columns - 1) / columns);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{columns * Swatch}\" height=\"{rows * Swatch}\" viewBox=\"0 0 {columns * Swatch} {rows * Swatch}\" shape-rendering=\"crispEdges\">\n");
        sb.Append("<title>").Append(System.Security.SecurityElement.Escape(Name ?? fallbackName)).Append("</title>\n");
        for (var i = 0; i < Colors.Count; i++)
        {
            var color = Colors[i];
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{i % columns * Swatch}\" y=\"{i / columns * Swatch}\" width=\"{Swatch}\" height=\"{Swatch}\" fill=\"{color.Hex}\"/>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
