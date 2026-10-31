// A glyph-ID-preserving subset of a CFF font that stays CFF, for fonts already embedded in a PDF (FontFile3): the
// charstrings of unused glyphs become a bare endchar and the subroutines no used glyph calls become a bare return, so
// glyph IDs, CIDs, the charset and the subroutine numbers stay valid. Everything else (dictionaries, strings, charset,
// encoding, FDSelect) is copied; only the offsets between the parts are written again, each as a five-byte integer.
// Converting to TrueType instead (FontSubsetter) would change the PDF's font type and make the outlines bigger.

using System.Buffers.Binary;

namespace Filee.Engines.Fonts.Cff;

/// <summary>Empties the glyphs and subroutines of a CFF font that the given glyphs don't use.</summary>
internal static class CffSubsetter
{
    private const int OpCharset = 15;
    private const int OpEncoding = 16;
    private const int OpCharStrings = 17;
    private const int OpPrivate = 18;
    private const int OpSubrs = 19;
    private const int OpFdArray = 1236;
    private const int OpFdSelect = 1237;

    private const byte EndChar = 14;
    private const byte Return = 11;

    private sealed record DictEntry(int Op, byte[] Operands, double[] Values);

    private sealed class Private
    {
        public required List<DictEntry> Dict { get; init; }
        public required byte[][] Subrs { get; init; }
    }

    /// <summary>
    /// The font with only <paramref name="glyphs"/> (plus .notdef and the glyphs their accents draw) left drawable.
    /// </summary>
    /// <exception cref="InvalidDataException">The font is damaged or laid out in a way this can't rewrite.</exception>
    /// <exception cref="NotSupportedException">The font is CFF2 or uses Type 1 charstrings.</exception>
    public static byte[] Subset(byte[] cff, IEnumerable<int> glyphs)
    {
        var font = CffFont.Parse(cff);
        var interpreter = new Type2Interpreter(font);
        var usedGlobal = new HashSet<int>();
        var usedLocal = new Dictionary<byte[][], HashSet<int>>(ReferenceEqualityComparer.Instance);
        interpreter.SubroutineCalled = (subrs, index) =>
        {
            if (ReferenceEquals(subrs, font.GlobalSubrs))
                usedGlobal.Add(index);
            else
                (usedLocal.TryGetValue(subrs, out var set) ? set : usedLocal[subrs] = []).Add(index);
        };
        var kept = new HashSet<int> { 0 };
        var pending = new Stack<int>(glyphs.Append(0));
        interpreter.AccentGlyphUsed = glyph => pending.Push(glyph);
        var run = new HashSet<int>();
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph < 0 || glyph >= font.GlyphCount || !run.Add(glyph))
                continue;
            kept.Add(glyph);
            interpreter.Run(glyph);
        }

        // The table's parts, read again as bytes.
        var position = (int)cff[2];
        var header = cff[..position];
        var nameStart = position;
        CffFont.ReadIndex(cff, ref position);
        var names = cff[nameStart..position];
        var topDicts = CffFont.ReadIndex(cff, ref position);
        var stringStart = position;
        CffFont.ReadIndex(cff, ref position);
        var strings = cff[stringStart..position];
        CffFont.ReadIndex(cff, ref position); // global subroutines: font.GlobalSubrs
        if (topDicts.Length != 1)
            throw new InvalidDataException("The CFF table holds more than one font.");

        var top = Tokens(topDicts[0]);
        var glyphCount = font.GlyphCount;
        var cs = (int)Value(top, OpCharset);
        var en = (int)Value(top, OpEncoding);
        var charset = cs >= 3 ? cff.AsSpan(cs, CharsetLength(cff, cs, glyphCount)).ToArray() : null;
        var encoding = en >= 2 ? cff.AsSpan(en, EncodingLength(cff, en)).ToArray() : null;
        var fdSelect = Find(top, OpFdSelect) is { } fs ? cff.AsSpan((int)fs.Values[0], FdSelectLength(cff, (int)fs.Values[0], glyphCount)).ToArray() : null;
        List<List<DictEntry>>? fontDicts = null;
        Private[] privates;
        if (Find(top, OpFdArray) is { } fdArray)
        {
            var at = (int)fdArray.Values[0];
            fontDicts = [.. CffFont.ReadIndex(cff, ref at).Select(Tokens)];
            privates = [.. fontDicts.Select(fd => ReadPrivate(cff, fd))];
        }
        else
        {
            privates = [ReadPrivate(cff, top)];
        }
        if (privates.Length != font.Privates.Length)
            throw new InvalidDataException("The CFF font dictionaries can't be matched.");

        var charStrings = new byte[glyphCount][];
        for (var gid = 0; gid < glyphCount; gid++)
            charStrings[gid] = kept.Contains(gid) ? font.CharStrings[gid] : [EndChar];
        var globalSubrs = Blank(font.GlobalSubrs, usedGlobal);
        var localSubrs = privates.Select((p, i) => Blank(p.Subrs, usedLocal.GetValueOrDefault(font.Privates[i].Subrs) ?? [])).ToArray();

        // Sizes first (every offset is written as five bytes, whatever its value), then the offsets.
        // A private dictionary's subroutines follow it directly.
        var privateDicts = privates.Select(p => Dict(p.Dict, new() { [OpSubrs] = [0] })).ToArray();
        var hasSubrs = privates.Select(p => Find(p.Dict, OpSubrs) is not null).ToArray();
        var privateBlocks = privates.Select((p, i) => privateDicts[i].Length + (hasSubrs[i] ? Index(localSubrs[i]).Length : 0)).ToArray();
        var topPlaceholder = TopValues(top, 0, 0, 0, 0, 0, 0, 0);
        var topIndexLength = Index([Dict(top, topPlaceholder)]).Length;
        var globalIndex = Index(globalSubrs);
        var charStringsIndex = Index(charStrings);

        var offset = header.Length + names.Length + topIndexLength + strings.Length + globalIndex.Length;
        var charsetAt = charset is null ? (int)Value(top, OpCharset) : offset;
        offset += charset?.Length ?? 0;
        var encodingAt = encoding is null ? (int)Value(top, OpEncoding) : offset;
        offset += encoding?.Length ?? 0;
        var fdSelectAt = offset;
        offset += fdSelect?.Length ?? 0;
        var charStringsAt = offset;
        offset += charStringsIndex.Length;

        byte[]? fdArrayIndex = null;
        var fdArrayAt = offset;
        var privateAt = new int[privates.Length];
        if (fontDicts is not null)
        {
            var fdLength = Index([.. fontDicts.Select(fd => Dict(fd, new() { [OpPrivate] = [0, 0] }))]).Length;
            var at = offset + fdLength;
            for (var i = 0; i < privates.Length; i++)
            {
                privateAt[i] = at;
                at += privateBlocks[i];
            }
            fdArrayIndex = Index([.. fontDicts.Select((fd, i) => Dict(fd, new() { [OpPrivate] = [privateDicts[i].Length, privateAt[i]] }))]);
            offset += fdArrayIndex.Length;
        }
        else
        {
            privateAt[0] = offset;
        }

        using var output = new MemoryStream(cff.Length);
        output.Write(header);
        output.Write(names);
        output.Write(Index([Dict(top, TopValues(top, charsetAt, encodingAt, charStringsAt, privateDicts[0].Length, privateAt[0], fdArrayAt, fdSelectAt))]));
        output.Write(strings);
        output.Write(globalIndex);
        if (charset is not null)
            output.Write(charset);
        if (encoding is not null)
            output.Write(encoding);
        if (fdSelect is not null)
            output.Write(fdSelect);
        output.Write(charStringsIndex);
        if (fdArrayIndex is not null)
            output.Write(fdArrayIndex);
        for (var i = 0; i < privates.Length; i++)
        {
            output.Write(Dict(privates[i].Dict, new() { [OpSubrs] = [privateDicts[i].Length] }));
            if (hasSubrs[i])
                output.Write(Index(localSubrs[i]));
        }
        return output.ToArray();
    }

    /// <summary>The Top DICT's offsets, for the operators it has.</summary>
    private static Dictionary<int, double[]> TopValues(List<DictEntry> top, int charset, int encoding, int charStrings,
        int privateSize, int privateOffset, int fdArray, int fdSelect)
    {
        var values = new Dictionary<int, double[]> { [OpCharStrings] = [charStrings] };
        if (Value(top, OpCharset) >= 3)
            values[OpCharset] = [charset];
        if (Value(top, OpEncoding) >= 2)
            values[OpEncoding] = [encoding];
        if (Find(top, OpPrivate) is not null)
            values[OpPrivate] = [privateSize, privateOffset];
        if (Find(top, OpFdArray) is not null)
            values[OpFdArray] = [fdArray];
        if (Find(top, OpFdSelect) is not null)
            values[OpFdSelect] = [fdSelect];
        return values;
    }

    private static Private ReadPrivate(byte[] cff, List<DictEntry> dict)
    {
        if (Find(dict, OpPrivate) is not { Values.Length: 2 } entry)
            return new Private { Dict = [], Subrs = [] };
        int size = (int)entry.Values[0], offset = (int)entry.Values[1];
        if (size < 0 || offset < 0 || offset + size > cff.Length)
            throw new InvalidDataException("The CFF private dictionary lies outside the table.");
        var tokens = Tokens(cff.AsSpan(offset, size).ToArray());
        var subrs = Array.Empty<byte[]>();
        if (Find(tokens, OpSubrs) is { } subrsEntry)
        {
            var at = offset + (int)subrsEntry.Values[0];
            subrs = CffFont.ReadIndex(cff, ref at);
        }
        return new Private { Dict = tokens, Subrs = subrs };
    }

    private static byte[][] Blank(byte[][] subrs, HashSet<int> used) =>
        [.. subrs.Select((s, i) => used.Contains(i) ? s : new[] { Return })];

    private static DictEntry? Find(List<DictEntry> dict, int op) => dict.FirstOrDefault(e => e.Op == op);

    private static double Value(List<DictEntry> dict, int op) => Find(dict, op) is { Values.Length: > 0 } e ? e.Values[0] : 0;

    /// <summary>A DICT's entries with their operands as written, so reals and deltas are copied exactly.</summary>
    private static List<DictEntry> Tokens(byte[] data)
    {
        var entries = new List<DictEntry>();
        var start = 0;
        var i = 0;
        while (i < data.Length)
        {
            int b0 = data[i];
            if (b0 <= 21)
            {
                var op = b0 == 12 && i + 1 < data.Length ? 1200 + data[i + 1] : b0;
                var operands = data[start..i];
                // The operands' values, read by CffFont's DICT parser behind a stand-in operator.
                entries.Add(new DictEntry(op, operands, CffFont.ParseDict([.. operands, (byte)OpCharStrings]).GetValueOrDefault(OpCharStrings, [])));
                i += b0 == 12 ? 2 : 1;
                start = i;
            }
            else
            {
                i += b0 switch
                {
                    28 => 3,
                    29 => 5,
                    30 => RealLength(data, i),
                    >= 247 and <= 254 => 2,
                    _ => 1,
                };
            }
        }
        return entries;
    }

    private static int RealLength(byte[] data, int start)
    {
        var i = start + 1;
        while (i < data.Length && (data[i] & 0x0F) != 0x0F && (data[i] >> 4) != 0x0F)
            i++;
        return i + 1 - start;
    }

    /// <summary>A DICT with the operands of <paramref name="replace"/>'s operators written as five-byte integers.</summary>
    private static byte[] Dict(List<DictEntry> dict, Dictionary<int, double[]> replace)
    {
        using var output = new MemoryStream();
        Span<byte> number = stackalloc byte[5];
        foreach (var entry in dict)
        {
            if (replace.TryGetValue(entry.Op, out var values))
            {
                foreach (var value in values)
                {
                    number[0] = 29;
                    BinaryPrimitives.WriteInt32BigEndian(number[1..], (int)value);
                    output.Write(number);
                }
            }
            else
            {
                output.Write(entry.Operands);
            }
            if (entry.Op >= 1200)
            {
                output.WriteByte(12);
                output.WriteByte((byte)(entry.Op - 1200));
            }
            else
            {
                output.WriteByte((byte)entry.Op);
            }
        }
        return output.ToArray();
    }

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0)
            return [0, 0];
        var total = items.Sum(i => (long)i.Length) + 1;
        var offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
        using var output = new MemoryStream();
        output.WriteByte((byte)(items.Length >> 8));
        output.WriteByte((byte)items.Length);
        output.WriteByte((byte)offSize);
        long offset = 1;
        for (var i = 0; i <= items.Length; i++)
        {
            for (var b = offSize - 1; b >= 0; b--)
                output.WriteByte((byte)(offset >> (8 * b)));
            if (i < items.Length)
                offset += items[i].Length;
        }
        foreach (var item in items)
            output.Write(item);
        return output.ToArray();
    }

    private static int CharsetLength(byte[] cff, int offset, int glyphCount)
    {
        var format = cff[offset];
        if (format == 0)
            return 1 + 2 * Math.Max(0, glyphCount - 1);
        if (format is not (1 or 2))
            throw new InvalidDataException($"Unknown CFF charset format {format}.");
        var at = offset + 1;
        var covered = 1;
        while (covered < glyphCount)
        {
            var left = format == 1 ? cff[at + 2] : BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(at + 2));
            covered += left + 1;
            at += format == 1 ? 3 : 4;
        }
        return at - offset;
    }

    private static int EncodingLength(byte[] cff, int offset)
    {
        var format = cff[offset];
        var length = (format & 0x7F) switch
        {
            0 => 2 + cff[offset + 1],
            1 => 2 + 2 * cff[offset + 1],
            _ => throw new InvalidDataException($"Unknown CFF encoding format {format}."),
        };
        if ((format & 0x80) != 0)
            length += 1 + 3 * cff[offset + length];
        return length;
    }

    private static int FdSelectLength(byte[] cff, int offset, int glyphCount) => cff[offset] switch
    {
        0 => 1 + glyphCount,
        3 => 1 + 2 + 3 * BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(offset + 1)) + 2,
        var format => throw new InvalidDataException($"Unknown CFF FDSelect format {format}."),
    };
}
