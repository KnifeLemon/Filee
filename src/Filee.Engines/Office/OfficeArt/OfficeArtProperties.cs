// OfficeArt shapes and their property tables ([MS-ODRAW] 2.2.9, 2.3): an SpContainer holds the shape's FSP (type in
// the instance, shape id, flags) and its property tables (OfficeArtFOPT, and the secondary and tertiary ones). A
// property table's instance is its number of properties; each is an opid (pid in 14 bits, fBid, fComplex) and a 32-bit
// value, and the complex properties' data (arrays such as pVertices, strings) follows all of them, in their order, each
// as long as its value says. Readers look properties up by pid; a pid not in the table has its default.

using System.Buffers.Binary;

namespace Filee.Engines.Office.OfficeArt;

/// <summary>A shape read from its SpContainer: the FSP, the merged property tables, and all of its records.</summary>
internal sealed class OfficeArtShape
{
    // FSP flags.
    public const uint Group = 0x1;
    public const uint Child = 0x2;
    public const uint Patriarch = 0x4;
    public const uint Deleted = 0x8;
    public const uint FlipH = 0x40;
    public const uint FlipV = 0x80;
    public const uint Background = 0x400;

    /// <summary>The shape id (spid) from the FSP, or null when the container has none.</summary>
    public int? Id { get; private set; }

    /// <summary>The shape type (MSOSPT): 1 rectangle, 3 ellipse, 20 line, 75 picture frame, 202 text box, ...</summary>
    public int Type { get; private set; }

    public uint Flags { get; private set; }

    /// <summary>Simple property values by pid.</summary>
    public Dictionary<int, uint> Properties { get; } = [];

    /// <summary>Where the data of each complex property is, by pid (in the bytes the shape was read from).</summary>
    public Dictionary<int, (int Start, int Length)> Complex { get; } = [];

    /// <summary>The records of the SpContainer, in order (anchors, client data and text are the reader's).</summary>
    public List<OfficeArtRecord> Records { get; } = [];

    /// <summary>
    /// Reads the shape of <paramref name="container"/>. The primary and secondary property tables overwrite what came
    /// before; the tertiary one holds other flags under the same pids and only adds what is not there yet.
    /// </summary>
    public static OfficeArtShape Read(ReadOnlySpan<byte> bytes, OfficeArtRecord container)
    {
        var shape = new OfficeArtShape();
        foreach (var child in OfficeArtRecords.Children(bytes, container))
        {
            shape.Records.Add(child);
            switch (child.Type)
            {
                case OfficeArtRecords.Fsp when child.Length >= 8:
                    shape.Type = child.Instance;
                    shape.Id = BinaryPrimitives.ReadInt32LittleEndian(bytes[child.Start..]);
                    shape.Flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(child.Start + 4)..]);
                    break;
                case OfficeArtRecords.Fopt or OfficeArtRecords.SecondaryFopt:
                    OfficeArtProperties.Read(bytes, child, shape.Properties, complex: shape.Complex);
                    break;
                case OfficeArtRecords.TertiaryFopt:
                    OfficeArtProperties.Read(bytes, child, shape.Properties, overwrite: false, complex: shape.Complex);
                    break;
            }
        }
        return shape;
    }
}

/// <summary>Property tables and the pids readers use.</summary>
internal static class OfficeArtProperties
{
    // Transform.
    public const int Rotation = 0x0004;

    // Text box insets (EMU).
    public const int TextLeft = 0x0081;
    public const int TextTop = 0x0082;
    public const int TextRight = 0x0083;
    public const int TextBottom = 0x0084;

    // Picture: crops (16.16 fractions of the picture) and the BLIP's 1-based index in the BStore.
    public const int CropFromTop = 0x0100;
    public const int CropFromBottom = 0x0101;
    public const int CropFromLeft = 0x0102;
    public const int CropFromRight = 0x0103;
    public const int Pib = 0x0104;

    // Geometry.
    public const int GeoLeft = 0x0140;
    public const int GeoTop = 0x0141;
    public const int GeoRight = 0x0142;
    public const int GeoBottom = 0x0143;
    public const int Vertices = 0x0145;
    public const int SegmentInfo = 0x0146;

    // Fill.
    public const int FillType = 0x0180;
    public const int FillColor = 0x0181;
    public const int FillOpacity = 0x0182;
    public const int FillBackColor = 0x0183;
    public const int FillBlip = 0x0186;
    public const int FillBooleans = 0x01BF;

    // Line.
    public const int LineColor = 0x01C0;
    public const int LineWidth = 0x01CB;
    public const int LineBooleans = 0x01FF;

    // Position relative to the anchor (Word).
    public const int PosH = 0x038F;
    public const int PosRelH = 0x0390;
    public const int PosV = 0x0391;
    public const int PosRelV = 0x0392;
    public const int GroupBooleans = 0x03BF;

    /// <summary>
    /// Reads the property table <paramref name="fopt"/> into <paramref name="properties"/> (simple values by pid) and,
    /// when given, <paramref name="complex"/> (where each complex property's data is). Entries and data that don't fit
    /// in the record are left out. With <paramref name="overwrite"/> off, pids already there keep their value.
    /// </summary>
    public static void Read(ReadOnlySpan<byte> bytes, OfficeArtRecord fopt, Dictionary<int, uint> properties, bool overwrite = true,
        Dictionary<int, (int Start, int Length)>? complex = null)
    {
        var count = 0;
        for (var i = 0; i < fopt.Instance && fopt.Start + i * 6 + 6 <= fopt.End; i++)
        {
            var opid = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(fopt.Start + i * 6)..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fopt.Start + i * 6 + 2)..]);
            if ((opid & 0x8000) == 0 && (overwrite || !properties.ContainsKey(opid & 0x3FFF)))
                properties[opid & 0x3FFF] = value;
            count++;
        }
        if (complex is null)
            return;
        // The complex data follows the entries, in their order.
        var at = fopt.Start + count * 6;
        for (var i = 0; i < count; i++)
        {
            var opid = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(fopt.Start + i * 6)..]);
            if ((opid & 0x8000) == 0)
                continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fopt.Start + i * 6 + 2)..]);
            if (length > (uint)(fopt.End - at))
                return;
            if (overwrite || !complex.ContainsKey(opid & 0x3FFF))
                complex[opid & 0x3FFF] = (at, (int)length);
            at += (int)length;
        }
    }

    /// <summary>
    /// A flag of a boolean property set: bit <paramref name="bit"/> when its "use" bit (16 higher) is set, otherwise
    /// <paramref name="fallback"/>.
    /// </summary>
    public static bool Flag(Dictionary<int, uint> properties, int pid, int bit, bool fallback) =>
        properties.TryGetValue(pid, out var value) && (value & (1u << (bit + 16))) != 0 ? (value & (1u << bit)) != 0 : fallback;

    /// <summary>
    /// A colour property as RRGGBB, when it is a plain RGB colour (OfficeArtCOLORREF with no scheme, system or palette
    /// index flags); null otherwise or when it is not set.
    /// </summary>
    public static string? Rgb(Dictionary<int, uint> properties, int pid) => properties.TryGetValue(pid, out var value) && (value >> 24) == 0
        ? $"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}" : null;
}
