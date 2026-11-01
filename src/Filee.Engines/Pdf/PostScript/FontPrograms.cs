// The glyph programs a PDF embeds, read for the PostScript writer: TrueType (FontFile2, or OpenType with glyf
// outlines) written as Type 42 fonts, and Type 1 (FontFile) and CFF (FontFile3: Type1C, CIDFontType0C, OpenType with
// CFF outlines) turned into outlines for Filee's own Type 1 fonts. Every program passes through Filee's parsers
// (SfntFont, CffFont, Type1Program); damage is an InvalidDataException.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A TrueType font program.</summary>
internal sealed class TrueTypeProgram
{
    private readonly byte[] _hmtx;
    private readonly int _metrics;

    private TrueTypeProgram(SfntFont font)
    {
        Font = font;
        GlyphCount = font.GlyphCount;
        UnitsPerEm = Math.Max(16, FontTables.UnitsPerEm(font.Require("head")));
        _hmtx = font.Require("hmtx");
        _metrics = BinaryPrimitives.ReadUInt16BigEndian(font.Require("hhea").AsSpan(34));
        Cmaps = font["cmap"] is { } cmap ? CmapTable.ReadAll(cmap) : [];
        PostNames = ReadPostNames(font["post"], GlyphCount);
    }

    public SfntFont Font { get; }

    public int GlyphCount { get; }

    public int UnitsPerEm { get; }

    public List<(int Platform, int Encoding, Dictionary<int, int> Map)> Cmaps { get; }

    /// <summary>Glyph names of a format 2 'post' table → glyph.</summary>
    public Dictionary<string, int> PostNames { get; }

    public static TrueTypeProgram Read(byte[] data)
    {
        var font = SfntFont.Read(data);
        if (!FontSubsetter.CanSubset(font, out var reason) || font["glyf"] is null)
            throw new InvalidDataException(reason ?? "The TrueType font has no glyph outlines.");
        // The glyph locations are read now, so writing the font later can't fail on them.
        GlyfTable.ReadLoca(font.Require("loca"), font.GlyphCount, FontTables.IndexToLocFormat(font.Require("head")) != 0, font.Require("glyf").Length);
        return new TrueTypeProgram(font);
    }

    /// <summary>The advance of <paramref name="glyph"/> in em.</summary>
    public double Advance(int glyph)
    {
        if (_metrics == 0)
            return 0;
        var index = Math.Min(glyph, _metrics - 1);
        return 4 * index + 2 <= _hmtx.Length ? BinaryPrimitives.ReadUInt16BigEndian(_hmtx.AsSpan(4 * index)) / (double)UnitsPerEm : 0;
    }

    /// <summary>The cmap subtable of a platform and encoding, or null.</summary>
    public Dictionary<int, int>? Cmap(int platform, int encoding) =>
        Cmaps.FirstOrDefault(c => c.Platform == platform && c.Encoding == encoding).Map;

    private static Dictionary<string, int> ReadPostNames(byte[]? post, int glyphCount)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        if (post is not { Length: > 34 } || BinaryPrimitives.ReadUInt32BigEndian(post) != 0x00020000)
            return names;
        int count = BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(32));
        var stringsAt = 34 + 2 * count;
        if (stringsAt > post.Length)
            return names;
        var strings = new List<string>();
        for (var at = stringsAt; at < post.Length;)
        {
            int length = post[at];
            if (at + 1 + length > post.Length)
                break;
            strings.Add(Encoding.Latin1.GetString(post, at + 1, length));
            at += 1 + length;
        }
        for (var glyph = 0; glyph < Math.Min(count, glyphCount); glyph++)
        {
            int index = BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(34 + 2 * glyph));
            var name = index < 258 ? MacGlyphName(index) : index - 258 < strings.Count ? strings[index - 258] : null;
            if (name is not null)
                names.TryAdd(name, glyph);
        }
        return names;
    }

    /// <summary>The standard Macintosh glyph names a 'post' table refers to by index (the ones simple fonts use).</summary>
    private static string? MacGlyphName(int index)
    {
        // Indexes 3–96 are space … asciitilde in ASCII order; the others matter little for PDF encodings.
        if (index is >= 3 and <= 96)
        {
            var c = (char)(index - 3 + 32);
            return UglyToad.PdfPig.Fonts.GlyphList.AdobeGlyphList.UnicodeCodePointToName(c);
        }
        return index == 0 ? ".notdef" : null;
    }
}

/// <summary>A CFF font program, its glyphs interpreted into outlines.</summary>
internal sealed class CffProgram
{
    private readonly CffFont _font;
    private readonly Type2Interpreter _interpreter;
    private Dictionary<string, int>? _glyphByName;

    private CffProgram(byte[] data, CffFont font)
    {
        Data = data;
        _font = font;
        _interpreter = new Type2Interpreter(font);
    }

    public byte[] Data { get; }

    public bool IsCid => _font.IsCid;

    public int GlyphCount => _font.GlyphCount;

    /// <summary>Reads a bare CFF program or the 'CFF ' table of an OpenType font.</summary>
    public static CffProgram Read(byte[] data)
    {
        if (SfntFont.IsSfnt(data))
            data = SfntFont.Read(data)["CFF "] ?? throw new InvalidDataException("The OpenType font has no CFF outlines.");
        try
        {
            return new CffProgram(data, CffFont.Parse(data));
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>The glyph of a CID (CID-keyed fonts map CIDs through their charset; name-keyed ones use them as glyph IDs).</summary>
    public int GlyphOfCid(int cid)
    {
        if (!_font.IsCid || _font.Charset is null)
            return cid < _font.GlyphCount ? cid : 0;
        for (var gid = 0; gid < _font.Charset.Length; gid++)
        {
            if (_font.Charset[gid] == cid)
                return gid;
        }
        return 0;
    }

    /// <summary>The glyph called <paramref name="name"/>, or -1.</summary>
    public int GlyphOfName(string name)
    {
        if (_glyphByName is null)
        {
            _glyphByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var gid = 0; gid < _font.GlyphCount; gid++)
            {
                if (_font.GlyphName(gid) is { } glyphName)
                    _glyphByName.TryAdd(glyphName, gid);
            }
        }
        return _glyphByName.GetValueOrDefault(name, -1);
    }

    /// <summary>The built-in encoding of a name-keyed font: code → glyph name.</summary>
    public string?[] BuiltInEncoding()
    {
        var names = new string?[256];
        if (_font.IsCid)
            return names;
        var at = (int)Data[2];
        CffFont.ReadIndex(Data, ref at); // Name INDEX
        var top = CffFont.ParseDict(CffFont.ReadIndex(Data, ref at)[0]);
        var offset = top.TryGetValue(16, out var encoding) && encoding.Length > 0 ? (int)encoding[0] : 0;
        if (offset is 0 or 1)
        {
            // Standard (and Expert, which has no glyphs PDFs draw by name here): codes by StandardEncoding.
            for (var code = 0; code < 256; code++)
            {
                var sid = CffStandardData.StandardEncoding[code];
                if (sid != 0)
                    names[code] = CffStandardData.StandardStrings[sid];
            }
            return names;
        }
        if (offset < 0 || offset >= Data.Length)
            throw new InvalidDataException("The CFF encoding lies outside the font.");
        var r = new BigEndianReader(Data, offset);
        var format = r.U8();
        var glyph = 1;
        switch (format & 0x7F)
        {
            case 0:
                int count = r.U8();
                for (var i = 0; i < count; i++, glyph++)
                    names[r.U8()] = _font.GlyphName(glyph);
                break;
            case 1:
                int ranges = r.U8();
                for (var i = 0; i < ranges; i++)
                {
                    int first = r.U8();
                    int left = r.U8();
                    for (var k = 0; k <= left; k++, glyph++)
                    {
                        if (first + k < 256)
                            names[first + k] = _font.GlyphName(glyph);
                    }
                }
                break;
            default:
                throw new InvalidDataException("The CFF encoding has an unknown format.");
        }
        if ((format & 0x80) != 0)
        {
            int supplements = r.U8();
            for (var i = 0; i < supplements; i++)
            {
                int code = r.U8();
                int sid = r.U16();
                var name = sid < CffStandardData.StandardStrings.Length ? CffStandardData.StandardStrings[sid]
                    : sid - CffStandardData.StandardStrings.Length < _font.Strings.Length ? _font.Strings[sid - CffStandardData.StandardStrings.Length] : null;
                names[code] = name;
            }
        }
        return names;
    }

    /// <summary>The outline of <paramref name="glyph"/> in a 1000-unit em.</summary>
    public List<PathContour> Outline(int glyph)
    {
        if (glyph < 0 || glyph >= _font.GlyphCount)
            return [];
        lock (_interpreter)
        {
            var contours = _interpreter.Run(glyph);
            var matrix = _font.FontMatrices[Math.Min(_font.FontDictIndex(glyph), _font.FontMatrices.Length - 1)];
            return Type1Writer.ToThousandUnits(contours, matrix);
        }
    }
}

/// <summary>Writes Type 42 fonts (TrueType inside PostScript).</summary>
internal static class Type42Writer
{
    /// <summary>Strings of the sfnts array stay under PostScript's 65,535-byte limit, split where tables or glyphs start.</summary>
    private const int MaxString = 65_532;

    /// <summary>The tables a Type 42 font needs; the cmap, names and layout tables stay behind.</summary>
    private static readonly string[] Tables = ["head", "hhea", "hmtx", "loca", "glyf", "maxp", "cvt ", "fpgm", "prep", "post", "OS/2"];

    /// <summary>
    /// Writes <c>/<paramref name="variable"/> [&lt;…&gt; …] def</c>: the font's tables (subset to <paramref name="glyphs"/>
    /// when Filee can, glyph IDs kept) for one or more Type 42 fonts to share.
    /// </summary>
    public static void WriteSfnts(PsWriter w, string variable, TrueTypeProgram program, IEnumerable<int> glyphs)
    {
        SfntFont font;
        try
        {
            font = FontSubsetter.Subset(program.Font, glyphs.Where(g => g < program.GlyphCount), keepGlyphCount: true);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            font = program.Font;
        }
        var kept = new SfntFont(SfntFont.TrueTypeFlavor);
        foreach (var tag in Tables)
        {
            if (font[tag] is { } table)
                kept.Tables[tag] = table;
        }
        var bytes = kept.Write();
        w.NewLine().Name(variable).Token("[");
        foreach (var (start, length) in Pieces(bytes, kept))
        {
            w.NewLine();
            w.Hex(bytes.AsSpan(start, length));
        }
        w.Token("]").Token("def").NewLine();
    }

    /// <summary>Pieces of the font file that start at tables or glyphs, each of even length and at most <see cref="MaxString"/> bytes.</summary>
    private static List<(int Start, int Length)> Pieces(byte[] file, SfntFont font)
    {
        var cuts = new SortedSet<int> { 0, file.Length };
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        int glyfOffset = -1, glyfLength = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + 16 * i;
            var tag = SfntFont.TagName(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry)));
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 12));
            cuts.Add(offset);
            if (tag == "glyf")
                (glyfOffset, glyfLength) = (offset, length);
        }
        var glyphStarts = new List<int>();
        if (glyfOffset >= 0 && font["loca"] is { } loca && font["head"] is { } head)
        {
            var longLoca = FontTables.IndexToLocFormat(head) != 0;
            var glyphs = GlyfTable.ReadLoca(loca, font.GlyphCount, longLoca, glyfLength);
            glyphStarts.AddRange(glyphs.Select(g => glyfOffset + g.Offset));
        }
        var pieces = new List<(int, int)>();
        var list = cuts.ToList();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            int start = list[i], end = list[i + 1];
            while (end - start > MaxString)
            {
                // split a large table (glyf) at the last glyph that starts within reach, else anywhere even
                var limit = start + MaxString;
                var cut = glyphStarts.Where(g => g > start && g <= limit && (g - start) % 2 == 0).DefaultIfEmpty(start + MaxString).Max();
                pieces.Add((start, cut - start));
                start = cut;
            }
            if (end > start)
                pieces.Add((start, end - start));
        }
        return pieces;
    }

    /// <summary>
    /// Writes a Type 42 font resource named <paramref name="name"/> on the shared tables <paramref name="sfnts"/>:
    /// code N draws glyph <c>/aN</c>, which is glyph ID <paramref name="glyphs"/>[N].
    /// </summary>
    public static void WriteFont(PsWriter w, string name, string sfnts, TrueTypeProgram program, IReadOnlyDictionary<int, int> glyphs)
    {
        var head = program.Font.Require("head");
        double Em(int at) => BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(at)) / (double)program.UnitsPerEm;
        w.Line($"%%BeginResource: font {name}");
        w.Line("11 dict begin");
        w.Line($"/FontName /{name} def /FontType 42 def /PaintType 0 def");
        w.Line("/FontMatrix [1 0 0 1 0 0] def");
        w.NewLine().Name("FontBBox").Array([Em(36), Em(38), Em(40), Em(42)]).Token("def");
        w.Line("/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for");
        foreach (var code in glyphs.Keys.Order())
            w.Token($"dup {code} /a{code} put");
        w.Line("readonly def");
        w.Line($"/CharStrings {glyphs.Count + 1} dict dup begin /.notdef 0 def");
        foreach (var (code, glyph) in glyphs.OrderBy(g => g.Key))
            w.Token($"/a{code} {glyph} def");
        w.Line("end readonly def");
        w.Line($"/sfnts {sfnts} def");
        w.Line("FontName currentdict end definefont pop");
        w.Line("%%EndResource");
    }
}
