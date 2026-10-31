// The 'cmap' table (character → glyph): reads the font's best Unicode subtable (formats 4, 6, 10, 12, 13; a symbol
// font's (3,0) subtable when it has no Unicode one) and writes a new table from a code point → glyph map, as format 4
// for the BMP plus format 12 when the map reaches beyond it. Used by the font subsetter.

using System.Buffers.Binary;

namespace Filee.Engines.Fonts;

/// <summary>Reads and writes the character-to-glyph map of a font.</summary>
internal static class CmapTable
{
    /// <summary>Highest code point a format 4 subtable can hold.</summary>
    private const int BmpLast = 0xFFFF;

    /// <summary>
    /// The code point → glyph map of the best subtable: full Unicode (3,10 / 0,4 / 0,6) before BMP Unicode (3,1 / 0,x)
    /// before symbol (3,0), whose codes are returned as stored (usually U+F020–U+F0FF). Empty without a usable subtable.
    /// </summary>
    public static Dictionary<int, int> Read(byte[] cmap, out bool symbol)
    {
        symbol = false;
        var r = new BigEndianReader(cmap);
        r.Skip(2); // version
        int count = r.U16();
        (int Rank, int Offset, bool Symbol) best = (int.MaxValue, -1, false);
        for (var i = 0; i < count; i++)
        {
            int platform = r.U16();
            int encoding = r.U16();
            var offset = r.U32();
            if (offset > cmap.Length - 4)
                continue;
            var format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan((int)offset));
            var rank = (platform, encoding) switch
            {
                (3, 10) or (0, 4) or (0, 6) => 0,
                (3, 1) or (0, 3) or (0, 2) or (0, 1) or (0, 0) => 1,
                (3, 0) => 2,
                _ => int.MaxValue,
            };
            if (format is not (4 or 6 or 10 or 12 or 13))
                rank = int.MaxValue;
            if (rank < best.Rank)
                best = (rank, (int)offset, platform == 3 && encoding == 0);
        }
        var map = new Dictionary<int, int>();
        if (best.Offset < 0)
            return map;
        symbol = best.Symbol;
        ReadSubtable(cmap, best.Offset, map);
        return map;
    }

    /// <summary>
    /// Every subtable's map with its platform and encoding, as a PDF viewer picks one for a simple TrueType font
    /// ((3,1), (3,0) or (1,0), whose byte-encoding format 0 is read too).
    /// </summary>
    public static List<(int Platform, int Encoding, Dictionary<int, int> Map)> ReadAll(byte[] cmap)
    {
        var result = new List<(int, int, Dictionary<int, int>)>();
        var r = new BigEndianReader(cmap);
        r.Skip(2); // version
        int count = r.U16();
        for (var i = 0; i < count; i++)
        {
            int platform = r.U16();
            int encoding = r.U16();
            var offset = r.U32();
            if (offset > cmap.Length - 4)
                continue;
            var map = new Dictionary<int, int>();
            ReadSubtable(cmap, (int)offset, map);
            result.Add((platform, encoding, map));
        }
        return result;
    }

    private static void ReadSubtable(byte[] cmap, int offset, Dictionary<int, int> map)
    {
        var r = new BigEndianReader(cmap, offset);
        int format = r.U16();
        switch (format)
        {
            case 0:
                r.Skip(4); // length, language
                for (var code = 0; code < 256 && r.Remaining > 0; code++)
                    Add(map, code, r.U8());
                break;
            case 4:
                ReadFormat4(cmap, offset, map);
                break;
            case 6:
                r.Skip(4); // length, language
                ReadTrimmed(r, r.U16(), r.U16(), map);
                break;
            case 10:
                r.Skip(10); // reserved, length, language
                ReadTrimmed(r, r.U32(), r.U32(), map);
                break;
            case 12 or 13:
                r.Skip(10); // reserved, length, language
                ReadGroups(r, sameGlyph: format == 13, map);
                break;
        }
    }

    /// <summary>Formats 6 and 10: a glyph for each code of one range.</summary>
    private static void ReadTrimmed(BigEndianReader r, uint first, uint count, Dictionary<int, int> map)
    {
        if (count > (uint)r.Remaining / 2)
            throw new InvalidDataException("The font's character map is damaged.");
        for (uint i = 0; i < count; i++)
            Add(map, (int)Math.Min(first + i, int.MaxValue), r.U16());
    }

    /// <summary>Formats 12 (consecutive glyphs per group) and 13 (one glyph per group).</summary>
    private static void ReadGroups(BigEndianReader r, bool sameGlyph, Dictionary<int, int> map)
    {
        var groups = r.U32();
        if (groups > (uint)r.Remaining / 12)
            throw new InvalidDataException("The font's character map is damaged.");
        for (uint g = 0; g < groups; g++)
        {
            var start = r.U32();
            var end = Math.Min(r.U32(), 0x10FFFF);
            var glyph = r.U32();
            for (var c = start; c <= end; c++)
                Add(map, (int)c, sameGlyph ? glyph : glyph + (c - start));
        }
    }

    private static void ReadFormat4(byte[] cmap, int offset, Dictionary<int, int> map)
    {
        var r = new BigEndianReader(cmap, offset + 6);
        var segments = r.U16() / 2;
        var ends = offset + 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var rangeOffsets = deltas + segments * 2;
        if (rangeOffsets + segments * 2 > cmap.Length)
            throw new InvalidDataException("The font's character map is damaged.");
        for (var s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(ends + s * 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(starts + s * 2));
            int delta = BinaryPrimitives.ReadInt16BigEndian(cmap.AsSpan(deltas + s * 2));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rangeOffsets + s * 2));
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = rangeOffsets + s * 2 + rangeOffset + (c - start) * 2;
                    if (at < 0 || at + 2 > cmap.Length)
                        continue;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(at));
                    if (glyph != 0)
                        glyph = (glyph + delta) & 0xFFFF;
                }
                Add(map, c, (uint)glyph);
            }
        }
    }

    private static void Add(Dictionary<int, int> map, int code, uint glyph)
    {
        if (glyph is > 0 and <= 0xFFFF)
            map.TryAdd(code, (int)glyph);
    }

    /// <summary>
    /// A cmap table for <paramref name="map"/>: (3,1) format 4 with the BMP (or (3,0) for a symbol font) and, when
    /// the map has code points above U+FFFF, (3,10) format 12 with all of them.
    /// </summary>
    public static byte[] Build(IReadOnlyDictionary<int, int> map, bool symbol = false)
    {
        var sorted = map.Where(p => p.Key >= 0 && p.Value > 0).OrderBy(p => p.Key).ToList();
        var format4 = Format4([.. sorted.Where(p => p.Key < BmpLast)]);
        var full = !symbol && sorted.Any(p => p.Key > BmpLast) ? Format12(sorted) : null;

        var w = new BigEndianWriter(12 + format4.Length + (full?.Length ?? 0));
        w.U16(0); // version
        w.U16(full is null ? 1 : 2);
        var offset = 4 + 8 * (full is null ? 1 : 2);
        w.U16(3);
        w.U16(symbol ? 0 : 1);
        w.U32((uint)offset);
        if (full is not null)
        {
            w.U16(3);
            w.U16(10);
            w.U32((uint)(offset + format4.Length));
        }
        w.Bytes(format4);
        if (full is not null)
            w.Bytes(full);
        return w.ToArray();
    }

    /// <summary>Format 4 with one segment per run of consecutive codes mapped to consecutive glyphs (idDelta only).</summary>
    private static byte[] Format4(List<KeyValuePair<int, int>> map)
    {
        var segments = new List<(int Start, int End, int Delta)>();
        foreach (var (code, glyph) in map)
        {
            if (segments.Count > 0 && segments[^1] is var last && last.End == code - 1 && last.Delta == glyph - code)
                segments[^1] = (last.Start, code, last.Delta);
            else
                segments.Add((code, code, glyph - code));
        }
        segments.Add((0xFFFF, 0xFFFF, 1)); // the required final segment

        var count = segments.Count;
        var length = 16 + 8 * count;
        if (length > 0xFFFF)
            throw new InvalidDataException("Too many characters for a format 4 character map.");
        var selector = count <= 1 ? 0 : System.Numerics.BitOperations.Log2((uint)count);
        var searchRange = 2 << selector;
        var w = new BigEndianWriter(length);
        w.U16(4);
        w.U16(length);
        w.U16(0); // language
        w.U16(count * 2);
        w.U16(searchRange);
        w.U16(selector);
        w.U16(count * 2 - searchRange);
        foreach (var s in segments)
            w.U16(s.End);
        w.U16(0); // reservedPad
        foreach (var s in segments)
            w.U16(s.Start);
        foreach (var s in segments)
            w.U16(s.Delta & 0xFFFF);
        foreach (var _ in segments)
            w.U16(0); // idRangeOffset
        return w.ToArray();
    }

    /// <summary>Format 12 with one group per run of consecutive codes mapped to consecutive glyphs.</summary>
    private static byte[] Format12(List<KeyValuePair<int, int>> map)
    {
        var groups = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in map)
        {
            if (groups.Count > 0 && groups[^1] is var last && last.End == code - 1 && last.Glyph + (code - last.Start) == glyph)
                groups[^1] = (last.Start, code, last.Glyph);
            else
                groups.Add((code, code, glyph));
        }
        var w = new BigEndianWriter(16 + 12 * groups.Count);
        w.U16(12);
        w.U16(0); // reserved
        w.U32((uint)(16 + 12 * groups.Count));
        w.U32(0); // language
        w.U32((uint)groups.Count);
        foreach (var g in groups)
        {
            w.U32((uint)g.Start);
            w.U32((uint)g.End);
            w.U32((uint)g.Glyph);
        }
        return w.ToArray();
    }
}
