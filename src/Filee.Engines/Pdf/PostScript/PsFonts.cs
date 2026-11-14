// The fonts of a PDF as PostScript fonts. Every shown glyph is placed by its PDF width (xshow), so the PostScript
// fonts only have to draw the right glyph at a code:
//
//  * simple fonts keep their codes: a font of Filee's own whose code N draws glyph /aN, the glyph the PDF font draws
//    at N (PDF 32000 9.6.5, 9.6.6). Embedded TrueType becomes a Type 42 font; embedded Type 1 and CFF become Type 1
//    fonts of Filee's own made from the interpreted outlines (no font program code is copied); fonts that aren't
//    embedded become the PostScript standard font closest to them, re-encoded;
//  * composite (Type 0) fonts with an Identity or an embedded CMap: their glyphs are spread over base fonts of 256
//    glyphs each (Type 42 sharing one copy of the TrueType tables, or Type 1), so no CIDFonts or CMaps are needed;
//  * Type 3 fonts become PostScript Type 3 fonts whose glyph procedures are the glyph streams translated like page
//    content.
//
// Fonts that can't be drawn this way (vertical writing, predefined CMaps, CID fonts that aren't embedded, simple CFF
// fonts without widths) make their page untranslatable, and the page is drawn as a picture instead.

using System.Text;
using Filee.Engines.Fonts.Cff;
using PdfSharp.Pdf;
using UglyToad.PdfPig.Fonts;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A page the writer can't translate faithfully; it is drawn as a picture instead.</summary>
internal sealed class PsUntranslatableException(string reason) : Exception(reason);

/// <summary>A shown glyph: the PostScript font and code it is drawn with, its advance per unit of font size, and whether word spacing applies.</summary>
internal readonly record struct ShownGlyph(string Font, byte Code, double Width, bool WordSpace);

/// <summary>A PDF font as PostScript fonts.</summary>
internal abstract class PsFont
{
    /// <summary>True for Type 3 fonts, which draw in every text rendering mode by running their glyphs.</summary>
    public virtual bool IsType3 => false;

    /// <summary>Turns a shown string into glyphs, registering each so its font draws it.</summary>
    public abstract void Show(ReadOnlySpan<byte> text, List<ShownGlyph> glyphs);

    /// <summary>Writes the font resources (with the glyphs shown so far) and stores each font under its name in FileeDict.</summary>
    public abstract void Write(PsWriter w);

    /// <summary>Keeps a font dictionary in FileeDict under its own name, so pages set it without looking it up.</summary>
    protected static void Store(PsWriter w, string name) => w.Line($"/{name} /{name} findfont def");
}

/// <summary>The fonts used in one PostScript file.</summary>
internal sealed class PsFontSet(string prefix, PsFontSet.GlyphTranslator translateGlyph)
{
    /// <summary>Glyphs (all fonts together) one file may hold.</summary>
    internal const int MaxGlyphs = 200_000;

    /// <summary>Translates a Type 3 glyph stream into PostScript (its resources, else the font's).</summary>
    public delegate string GlyphTranslator(PdfDictionary glyph, PdfDictionary? resources, int depth);

    private readonly Dictionary<PdfDictionary, PsFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly List<PsFont> _order = [];
    private int _glyphs;

    public GlyphTranslator TranslateGlyph => translateGlyph;

    /// <summary>Counts a newly drawn glyph against <see cref="MaxGlyphs"/>.</summary>
    public void CountGlyph()
    {
        if (++_glyphs > MaxGlyphs)
            throw new PsUntranslatableException("The document draws too many different glyphs.");
    }

    /// <summary>The PostScript font for a PDF font dictionary.</summary>
    /// <exception cref="PsUntranslatableException">The font can't be drawn in PostScript faithfully.</exception>
    public PsFont Get(PdfDictionary font, int depth)
    {
        if (_fonts.TryGetValue(font, out var known))
            return known;
        var name = $"{prefix}F{_fonts.Count + 1}";
        PsFont created;
        try
        {
            created = PdfObjects.Name(font, "/Subtype") switch
            {
                "Type0" => CompositeFont.Create(font, name, this),
                "Type3" => new Type3Font(font, name, this, depth),
                _ => SimpleFont.Create(font, name, this),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException
            or OverflowException or InvalidOperationException or EndOfStreamException)
        {
            throw new PsUntranslatableException($"A font can't be read ({ex.Message}).");
        }
        _fonts[font] = created;
        _order.Add(created);
        return created;
    }

    public void Write(PsWriter w)
    {
        // A Type 3 glyph may use further fonts while it is translated (they are registered before it is written).
        for (var i = 0; i < _order.Count; i++)
            _order[i].Write(w);
    }

    /// <summary>The descriptor's embedded program and its kind (FontFile, FontFile2, FontFile3/…), or null.</summary>
    internal static (string Kind, byte[] Data)? Program(PdfDictionary? descriptor)
    {
        const long limit = 64L * 1024 * 1024;
        if (PdfObjects.Dictionary(descriptor, "/FontFile") is { Stream: not null } type1)
            return ("Type1", PdfStreams.Decode(type1, limit));
        if (PdfObjects.Dictionary(descriptor, "/FontFile2") is { Stream: not null } trueType)
            return ("TrueType", PdfStreams.Decode(trueType, limit));
        if (PdfObjects.Dictionary(descriptor, "/FontFile3") is { Stream: not null } cff)
        {
            var data = PdfStreams.Decode(cff, limit);
            var subtype = PdfObjects.Name(cff, "/Subtype");
            if (subtype == "OpenType" && Fonts.SfntFont.IsSfnt(data) && Fonts.SfntFont.Read(data)["glyf"] is not null)
                return ("TrueType", data);
            return ("CFF", data);
        }
        return null;
    }
}

/// <summary>Glyph names of the PDF encodings, and the code → name table of a simple font.</summary>
internal static class SimpleEncodings
{
    public static string?[] Named(string? name) => name switch
    {
        "WinAnsiEncoding" => Table(UglyToad.PdfPig.Fonts.Encodings.WinAnsiEncoding.Instance),
        "MacRomanEncoding" => Table(UglyToad.PdfPig.Fonts.Encodings.MacRomanEncoding.Instance),
        "MacExpertEncoding" => Table(UglyToad.PdfPig.Fonts.Encodings.MacOsRomanEncoding.Instance),
        "StandardEncoding" => Table(UglyToad.PdfPig.Fonts.Encodings.StandardEncoding.Instance),
        "Symbol" => Table(UglyToad.PdfPig.Fonts.Encodings.SymbolEncoding.Instance),
        "ZapfDingbats" => Table(UglyToad.PdfPig.Fonts.Encodings.ZapfDingbatsEncoding.Instance),
        _ => Table(UglyToad.PdfPig.Fonts.Encodings.StandardEncoding.Instance),
    };

    private static string?[] Table(UglyToad.PdfPig.Fonts.Encodings.Encoding encoding)
    {
        var names = new string?[256];
        foreach (var (code, name) in encoding.CodeToNameMap)
        {
            if (code is >= 0 and < 256)
                names[code] = name;
        }
        return names;
    }

    /// <summary>
    /// The glyph name of every code (PDF 32000 9.6.5): the Encoding's base (a named encoding, else
    /// <paramref name="builtIn"/> when there is one, else StandardEncoding) with its Differences.
    /// </summary>
    public static string?[] Resolve(PdfDictionary font, string?[]? builtIn, out bool explicitEncoding, bool symbolic = true)
    {
        var encoding = PdfObjects.Get(font, "/Encoding");
        explicitEncoding = encoding is not null;
        string?[] names;
        switch (encoding)
        {
            case PdfName named:
                names = Named(named.Value.TrimStart('/'));
                break;
            case PdfDictionary dictionary:
                names = PdfObjects.Name(dictionary, "/BaseEncoding") is { } baseName ? Named(baseName)
                    : builtIn is not null ? (string?[])builtIn.Clone() : Named("StandardEncoding");
                if (PdfObjects.Array(dictionary, "/Differences") is { } differences)
                {
                    var code = 0;
                    foreach (var item in differences.Elements.Take(4096).Select(PdfObjects.Resolve))
                    {
                        if (PdfObjects.Number(item) is { } n)
                            code = (int)n;
                        else if (item is PdfName glyph && code is >= 0 and < 256)
                            names[code++] = glyph.Value.TrimStart('/');
                    }
                }
                break;
            default:
                // No encoding: a symbolic font's own, else StandardEncoding (as PDFium draws embedded Type 1 fonts).
                names = builtIn is not null && symbolic ? (string?[])builtIn.Clone() : Named("StandardEncoding");
                break;
        }
        return names;
    }

    /// <summary>The character a glyph name stands for (Adobe Glyph List, uniXXXX, uXXXX), or -1.</summary>
    public static int Unicode(string name)
    {
        var text = GlyphList.AdobeGlyphList.NameToUnicode(name);
        if (string.IsNullOrEmpty(text))
            return -1;
        return char.ConvertToUtf32(text, 0) is var c && c > 0 ? c : -1;
    }
}

/// <summary>Widths of a simple font (PDF 32000 9.6.2): Widths from FirstChar, MissingWidth, else a fallback.</summary>
internal static class SimpleWidths
{
    public static double?[] Read(PdfDictionary font, double scale)
    {
        var widths = new double?[256];
        var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
        if (PdfObjects.Array(font, "/Widths") is { } array)
        {
            for (var i = 0; i < array.Elements.Count && first + i < 256; i++)
            {
                if (first + i >= 0 && PdfObjects.Number(array.Elements[i]) is { } w)
                    widths[first + i] = w * scale;
            }
        }
        if (PdfObjects.Dictionary(font, "/FontDescriptor") is { } descriptor && PdfObjects.Number(PdfObjects.Get(descriptor, "/MissingWidth")) is { } missing)
        {
            for (var code = 0; code < 256; code++)
                widths[code] ??= missing * scale;
        }
        return widths;
    }
}

/// <summary>A simple font (Type 1, TrueType, CFF, or one of the standard fonts that isn't embedded).</summary>
internal sealed class SimpleFont : PsFont
{
    private enum Kind
    {
        Outline,
        TrueType,
        Standard,
    }

    private readonly string _name;
    private readonly PsFontSet _set;
    private readonly Kind _kind;
    private readonly string?[] _names;
    private readonly double[] _widths = new double[256];
    private readonly Dictionary<int, (List<PathContour> Contours, double Width)> _outlines = [];
    private readonly Dictionary<int, int> _glyphIds = [];
    private readonly HashSet<int> _used = [];
    private Type1Program? _type1;
    private CffProgram? _cff;
    private string?[]? _cffBuiltIn;
    private TrueTypeProgram? _trueType;
    private bool _symbolic;
    private bool _explicitEncoding;
    private string _standard = "Helvetica";

    private SimpleFont(string name, PsFontSet set, Kind kind, string?[] names)
    {
        _name = name;
        _set = set;
        _kind = kind;
        _names = names;
    }

    public static SimpleFont Create(PdfDictionary font, string name, PsFontSet set)
    {
        var descriptor = PdfObjects.Dictionary(font, "/FontDescriptor");
        var flags = (int)PdfObjects.Number(descriptor, "/Flags", 0);
        var symbolic = (flags & 4) != 0;
        var program = PsFontSet.Program(descriptor);
        var widths = SimpleWidths.Read(font, 0.001);
        SimpleFont result;
        switch (program)
        {
            case ("Type1", var data):
                {
                    var type1 = Type1Program.Read(data);
                    var names = SimpleEncodings.Resolve(font, type1.Encoding, out var explicitEncoding, symbolic);
                    result = new SimpleFont(name, set, Kind.Outline, names) { _type1 = type1, _explicitEncoding = explicitEncoding };
                    break;
                }
            case ("CFF", var data):
                {
                    var cff = CffProgram.Read(data);
                    var builtIn = cff.BuiltInEncoding();
                    var names = SimpleEncodings.Resolve(font, builtIn, out var explicitEncoding, symbolic);
                    result = new SimpleFont(name, set, Kind.Outline, names) { _cff = cff, _cffBuiltIn = builtIn, _explicitEncoding = explicitEncoding };
                    break;
                }
            case ("TrueType", var data):
                {
                    var trueType = TrueTypeProgram.Read(data);
                    var names = SimpleEncodings.Resolve(font, null, out var explicitEncoding);
                    result = new SimpleFont(name, set, Kind.TrueType, names) { _trueType = trueType, _explicitEncoding = explicitEncoding };
                    break;
                }
            default:
                {
                    var standard = StandardName(font, descriptor, flags);
                    string?[]? builtIn = standard is "Symbol" or "ZapfDingbats" ? SimpleEncodings.Named(standard) : null;
                    var names = SimpleEncodings.Resolve(font, builtIn, out var explicitEncoding);
                    result = new SimpleFont(name, set, Kind.Standard, names) { _standard = standard, _explicitEncoding = explicitEncoding };
                    break;
                }
        }
        result._symbolic = symbolic;
        var metrics = result._kind == Kind.Standard ? Standard14.GetAdobeFontMetrics(result._standard) : null;
        for (var code = 0; code < 256; code++)
        {
            if (widths[code] is { } w)
            {
                result._widths[code] = w;
                continue;
            }
            // No width in the PDF: the font's own.
            if (metrics is not null && result._names[code] is { } glyph && metrics.CharacterMetrics.TryGetValue(glyph, out var metric))
                result._widths[code] = metric.Width.X / 1000;
            else if (result._trueType is not null)
                result._widths[code] = result._trueType.Advance(result.TrueTypeGlyph(code));
            else if (result._type1 is not null && result.Type1Name(code) is { } type1Name)
                result._widths[code] = (result._type1.Outline(type1Name)?.Width ?? 0) * result._type1.FontMatrix[0];
            else if (result._cff is not null && PdfObjects.Array(font, "/Widths") is null)
                throw new PsUntranslatableException("A CFF font has no widths.");
        }
        return result;
    }

    /// <summary>The PostScript standard font closest to a font that isn't embedded.</summary>
    private static string StandardName(PdfDictionary font, PdfDictionary? descriptor, int flags)
    {
        var baseFont = PdfObjects.Name(font, "/BaseFont") ?? "";
        if (baseFont.Length > 7 && baseFont[6] == '+')
            baseFont = baseFont[7..];
        var compact = baseFont.Replace(" ", "", StringComparison.Ordinal);
        if (Standard14.GetMappedFontName(compact) is { Length: > 0 } mapped && Standard14.IsFontInStandard14(mapped))
            return mapped;
        if (compact.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
            return "Symbol";
        if (compact.Contains("Dingbats", StringComparison.OrdinalIgnoreCase))
            return "ZapfDingbats";
        var bold = compact.Contains("Bold", StringComparison.OrdinalIgnoreCase) || compact.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || (flags & (1 << 18)) != 0
            || PdfObjects.Number(descriptor, "/FontWeight", 400) >= 600;
        var italic = compact.Contains("Italic", StringComparison.OrdinalIgnoreCase) || compact.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
            || (flags & 64) != 0 || PdfObjects.Number(descriptor, "/ItalicAngle", 0) != 0;
        if (compact.Contains("Courier", StringComparison.OrdinalIgnoreCase) || compact.Contains("Mono", StringComparison.OrdinalIgnoreCase) || (flags & 1) != 0)
            return "Courier" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : "");
        if (compact.Contains("Times", StringComparison.OrdinalIgnoreCase) || (flags & 2) != 0)
            return "Times-" + (bold && italic ? "BoldItalic" : bold ? "Bold" : italic ? "Italic" : "Roman");
        return "Helvetica" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : "");
    }

    private string? Type1Name(int code)
    {
        if (_names[code] is { } name && _type1!.HasGlyph(name))
            return name;
        // A symbolic font draws a code its encoding names no glyph for with its own encoding.
        return _symbolic && _type1!.Encoding[code] is { } builtIn && _type1.HasGlyph(builtIn) ? builtIn : null;
    }

    /// <summary>The glyph a code of a simple TrueType font draws (PDF 32000 9.6.6.4, in the order viewers try).</summary>
    private int TrueTypeGlyph(int code)
    {
        var program = _trueType!;
        if ((!_symbolic || _explicitEncoding) && _names[code] is { } name)
        {
            if (program.Cmap(3, 1) is { } unicode && SimpleEncodings.Unicode(name) is var u and >= 0 && unicode.TryGetValue(u, out var g1))
                return g1;
            if (program.Cmap(1, 0) is { } mac && UglyToad.PdfPig.Fonts.Encodings.MacRomanEncoding.Instance.NameToCodeMap.TryGetValue(name, out var macCode)
                && mac.TryGetValue(macCode, out var g2))
                return g2;
            if (program.PostNames.TryGetValue(name, out var g3))
                return g3;
        }
        if (program.Cmap(3, 0) is { } symbol)
        {
            foreach (var key in (int[])[code, 0xF000 + code, 0xF100 + code, 0xF200 + code])
            {
                if (symbol.TryGetValue(key, out var g))
                    return g;
            }
        }
        if (program.Cmap(1, 0) is { } macintosh && macintosh.TryGetValue(code, out var g4))
            return g4;
        if (program.Cmap(3, 1) is { } windows && windows.TryGetValue(code, out var g5))
            return g5;
        return program.Cmaps.Count == 0 && code < program.GlyphCount ? code : 0;
    }

    public override void Show(ReadOnlySpan<byte> text, List<ShownGlyph> glyphs)
    {
        foreach (var b in text)
        {
            int code = b;
            if (!_used.Contains(code))
            {
                // The glyph is read first: a glyph that fails leaves the font as it was (its page becomes a picture).
                _set.CountGlyph();
                switch (_kind)
                {
                    case Kind.TrueType:
                        _glyphIds[code] = TrueTypeGlyph(code) is var gid && gid < _trueType!.GlyphCount ? gid : 0;
                        break;
                    case Kind.Outline when _type1 is not null:
                        _outlines[code] = Type1Name(code) is { } glyphName && _type1.Outline(glyphName) is { } outline
                            ? (Type1Writer.ToThousandUnits(outline.Contours, _type1.FontMatrix), 0)
                            : ([], 0);
                        break;
                    case Kind.Outline:
                        {
                            var cff = _cff!;
                            var glyph = _names[code] is { } n ? cff.GlyphOfName(n) : -1;
                            if (glyph < 0 && _symbolic && _cffBuiltIn?[code] is { } builtInName)
                                glyph = cff.GlyphOfName(builtInName);
                            if (glyph < 0 && cff.IsCid)
                                glyph = code < cff.GlyphCount ? code : 0;
                            _outlines[code] = (glyph > 0 ? cff.Outline(glyph) : [], 0);
                            break;
                        }
                }
                _used.Add(code);
            }
            glyphs.Add(new ShownGlyph(_name, b, _widths[code], code == 32));
        }
    }

    public override void Write(PsWriter w)
    {
        switch (_kind)
        {
            case Kind.TrueType:
                Type42Writer.WriteSfnts(w, _name + "s", _trueType!, _glyphIds.Values);
                Type42Writer.WriteFont(w, _name, _name + "s", _trueType!, _glyphIds);
                break;
            case Kind.Outline:
                Type1Writer.Write(w, _name, _outlines);
                break;
            default:
                // The standard font, re-encoded with the PDF's glyph names (names PostScript can't spell stay .notdef).
                w.Line($"%%IncludeResource: font {_standard}");
                w.Line($"/{_standard} findfont dup length dict begin {{1 index /FID ne {{def}} {{pop pop}} ifelse}} forall");
                w.Line("/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for");
                for (var code = 0; code < 256; code++)
                {
                    if (_names[code] is { } glyph && PsWriter.IsSafeName(glyph) && (_used.Contains(code) || code < 128))
                        w.Token($"dup {code} /{glyph} put");
                }
                w.Line("def currentdict end");
                w.Line($"/{_name} exch definefont pop");
                break;
        }
        Store(w, _name);
    }
}

/// <summary>A Type 0 font, its glyphs spread over base fonts of 256 glyphs.</summary>
internal sealed class CompositeFont : PsFont
{
    private const int MaxWidthEntries = 200_000;

    private readonly string _name;
    private readonly PsFontSet _set;
    private readonly CMapCodes _cmap;
    private readonly Dictionary<int, double> _widths;
    private readonly double _defaultWidth;
    private readonly Dictionary<int, (int Chunk, byte Code)> _slots = [];
    private readonly List<Dictionary<int, int>> _chunks = [];
    private readonly Dictionary<int, List<PathContour>> _outlines = [];
    private TrueTypeProgram? _trueType;
    private CffProgram? _cff;
    private byte[]? _cidToGid;

    private CompositeFont(string name, PsFontSet set, CMapCodes cmap, Dictionary<int, double> widths, double defaultWidth)
    {
        _name = name;
        _set = set;
        _cmap = cmap;
        _widths = widths;
        _defaultWidth = defaultWidth;
    }

    public static CompositeFont Create(PdfDictionary font, string name, PsFontSet set)
    {
        var cmap = CMapCodes.Read(PdfObjects.Get(font, "/Encoding"));
        if (PdfObjects.Array(font, "/DescendantFonts") is not { Elements.Count: > 0 } descendants
            || PdfObjects.Resolve(descendants.Elements[0]) is not PdfDictionary cidFont)
            throw new InvalidDataException("A Type 0 font has no descendant font.");
        var program = PsFontSet.Program(PdfObjects.Dictionary(cidFont, "/FontDescriptor"))
            ?? throw new PsUntranslatableException("A CID font isn't embedded.");
        var (widths, defaultWidth) = ReadWidths(cidFont);
        var result = new CompositeFont(name, set, cmap, widths, defaultWidth);
        switch (program)
        {
            case ("TrueType", var data):
                result._trueType = TrueTypeProgram.Read(data);
                if (PdfObjects.Get(cidFont, "/CIDToGIDMap") is PdfDictionary { Stream: not null } map)
                    result._cidToGid = PdfStreams.Decode(map, 2 * 65536 + 16);
                break;
            case ("CFF", var data):
                result._cff = CffProgram.Read(data);
                break;
            default:
                throw new PsUntranslatableException("A CID font has a Type 1 program.");
        }
        return result;
    }

    private static (Dictionary<int, double> Widths, double Default) ReadWidths(PdfDictionary cidFont)
    {
        var widths = new Dictionary<int, double>();
        var defaultWidth = PdfObjects.Number(cidFont, "/DW", 1000) / 1000;
        if (PdfObjects.Array(cidFont, "/W") is not { } array)
            return (widths, defaultWidth);
        var items = array.Elements.Select(PdfObjects.Resolve).ToList();
        for (var i = 0; i < items.Count;)
        {
            if (PdfObjects.Number(items[i]) is not { } first)
                break;
            if (i + 1 < items.Count && items[i + 1] is PdfArray list)
            {
                for (var k = 0; k < list.Elements.Count; k++)
                {
                    if (widths.Count >= MaxWidthEntries)
                        throw new InvalidDataException("A CID font has too many widths.");
                    widths[(int)first + k] = (PdfObjects.Number(list.Elements[k]) ?? 0) / 1000;
                }
                i += 2;
            }
            else if (i + 2 < items.Count && PdfObjects.Number(items[i + 1]) is { } last && PdfObjects.Number(items[i + 2]) is { } w)
            {
                if (last - first > MaxWidthEntries || widths.Count + (last - first) > MaxWidthEntries)
                    throw new InvalidDataException("A CID font has too many widths.");
                for (var cid = (int)first; cid <= (int)last; cid++)
                    widths[cid] = w / 1000;
                i += 3;
            }
            else
            {
                break;
            }
        }
        return (widths, defaultWidth);
    }

    private int Glyph(int cid)
    {
        if (_trueType is not null)
        {
            var gid = _cidToGid is null ? cid : 2 * cid + 1 < _cidToGid.Length ? _cidToGid[2 * cid] << 8 | _cidToGid[2 * cid + 1] : 0;
            return gid < _trueType.GlyphCount ? gid : 0;
        }
        return _cff!.GlyphOfCid(cid);
    }

    public override void Show(ReadOnlySpan<byte> text, List<ShownGlyph> glyphs)
    {
        foreach (var (cid, length, code) in _cmap.Decode(text))
        {
            var glyph = Glyph(cid);
            if (!_slots.TryGetValue(glyph, out var slot))
            {
                // The outline is read before the glyph gets its place, so a glyph that fails leaves the font as it was.
                _set.CountGlyph();
                if (_cff is not null)
                    _outlines[glyph] = _cff.Outline(glyph);
                if (_chunks.Count == 0 || _chunks[^1].Count == 256)
                    _chunks.Add([]);
                slot = (_chunks.Count - 1, (byte)_chunks[^1].Count);
                _chunks[^1][slot.Code] = glyph;
                _slots[glyph] = slot;
            }
            var width = _widths.TryGetValue(cid, out var w) ? w : _defaultWidth;
            // Word spacing applies to the single-byte code 32 (PDF 32000 9.3.3).
            glyphs.Add(new ShownGlyph($"{_name}_{slot.Chunk}", slot.Code, width, length == 1 && code == 32));
        }
    }

    public override void Write(PsWriter w)
    {
        if (_trueType is not null)
            Type42Writer.WriteSfnts(w, _name + "s", _trueType, _slots.Keys);
        for (var k = 0; k < _chunks.Count; k++)
        {
            var name = $"{_name}_{k}";
            if (_trueType is not null)
                Type42Writer.WriteFont(w, name, _name + "s", _trueType, _chunks[k]);
            else
                Type1Writer.Write(w, name, _chunks[k].ToDictionary(p => p.Key, p => (_outlines[p.Value], 0.0)));
            Store(w, name);
        }
    }
}

/// <summary>
/// The CMap of a Type 0 font: Identity-H (two-byte codes are CIDs), or an embedded CMap stream of code space ranges,
/// CID ranges and single CIDs. Vertical writing, predefined CMaps and CMaps that use another make the page untranslatable.
/// </summary>
internal sealed class CMapCodes
{
    private const int MaxEntries = 100_000;

    private readonly List<(byte[] Low, byte[] High)> _spaces = [];
    private readonly List<(uint Low, uint High, int Length, int Cid)> _ranges = [];
    private bool _identity;

    public static CMapCodes Read(PdfItem? encoding)
    {
        switch (encoding)
        {
            case PdfName { Value: "/Identity-H" }:
                return new CMapCodes { _identity = true };
            case PdfName name:
                throw new PsUntranslatableException($"The CMap {name.Value} isn't supported (vertical or predefined).");
            case PdfDictionary { Stream: not null } stream:
                if (PdfObjects.Number(stream, "/WMode", 0) != 0)
                    throw new PsUntranslatableException("Vertical writing isn't supported.");
                if (PdfObjects.Get(stream, "/UseCMap") is { } used)
                {
                    if (used is PdfName { Value: "/Identity-H" })
                        return new CMapCodes { _identity = true };
                    throw new PsUntranslatableException("An embedded CMap uses another CMap.");
                }
                return Parse(PdfStreams.Decode(stream, 16 * 1024 * 1024));
            default:
                throw new InvalidDataException("A Type 0 font has no encoding.");
        }
    }

    private static CMapCodes Parse(byte[] data)
    {
        var cmap = new CMapCodes();
        var lexer = new PdfContentLexer(data);
        var operands = new List<object?>();
        string? section = null;
        while (lexer.Next() is { } token)
        {
            if (token is PdfContentLexer.Operator op)
            {
                switch (op.Name)
                {
                    case "begincodespacerange" or "begincidrange" or "begincidchar":
                        section = op.Name;
                        break;
                    case "endcodespacerange":
                        for (var i = 0; i + 1 < operands.Count; i += 2)
                        {
                            if (operands[i] is byte[] low && operands[i + 1] is byte[] high && low.Length == high.Length && low.Length is > 0 and <= 4)
                                cmap._spaces.Add((low, high));
                        }
                        section = null;
                        break;
                    case "endcidrange":
                        for (var i = 0; i + 2 < operands.Count; i += 3)
                        {
                            if (operands[i] is byte[] low && operands[i + 1] is byte[] high && operands[i + 2] is double cid && low.Length == high.Length && low.Length is > 0 and <= 4)
                                cmap.Add(Value(low), Value(high), low.Length, (int)cid);
                        }
                        section = null;
                        break;
                    case "endcidchar":
                        for (var i = 0; i + 1 < operands.Count; i += 2)
                        {
                            if (operands[i] is byte[] code && operands[i + 1] is double cid && code.Length is > 0 and <= 4)
                                cmap.Add(Value(code), Value(code), code.Length, (int)cid);
                        }
                        section = null;
                        break;
                    case "usecmap":
                        throw new PsUntranslatableException("An embedded CMap uses another CMap.");
                    case "def" when operands.Count >= 2 && operands[^2] is PdfContentLexer.Name { Value: "WMode" } && operands[^1] is double mode && mode != 0:
                        throw new PsUntranslatableException("Vertical writing isn't supported.");
                }
                if (section is null || op.Name.StartsWith("begin", StringComparison.Ordinal))
                    operands.Clear();
                continue;
            }
            if (operands.Count > 3 * MaxEntries)
                throw new InvalidDataException("An embedded CMap is too long.");
            operands.Add(token);
        }
        if (cmap._spaces.Count == 0)
            cmap._spaces.Add(([0, 0], [0xFF, 0xFF]));
        return cmap;
    }

    private void Add(uint low, uint high, int length, int cid)
    {
        if (_ranges.Count >= MaxEntries || high < low)
            throw new InvalidDataException("An embedded CMap has too many ranges.");
        _ranges.Add((low, high, length, cid));
    }

    private static uint Value(ReadOnlySpan<byte> bytes)
    {
        uint value = 0;
        foreach (var b in bytes)
            value = value << 8 | b;
        return value;
    }

    /// <summary>The CIDs of a shown string, the length of each code and the code.</summary>
    public List<(int Cid, int Length, uint Code)> Decode(ReadOnlySpan<byte> text)
    {
        var result = new List<(int, int, uint)>(text.Length / 2 + 1);
        if (_identity)
        {
            for (var i = 0; i + 1 < text.Length; i += 2)
                result.Add((text[i] << 8 | text[i + 1], 2, (uint)(text[i] << 8 | text[i + 1])));
            return result;
        }
        for (var i = 0; i < text.Length;)
        {
            var length = CodeLength(text[i..]);
            var code = Value(text.Slice(i, length));
            var cid = 0;
            foreach (var (low, high, n, first) in _ranges)
            {
                if (n == length && code >= low && code <= high)
                {
                    cid = first + (int)(code - low);
                    break;
                }
            }
            result.Add((cid, length, code));
            i += length;
        }
        return result;
    }

    private int CodeLength(ReadOnlySpan<byte> text)
    {
        for (var length = 1; length <= 4 && length <= text.Length; length++)
        {
            foreach (var (low, high) in _spaces)
            {
                if (low.Length != length)
                    continue;
                var inside = true;
                for (var k = 0; k < length && inside; k++)
                    inside = text[k] >= low[k] && text[k] <= high[k];
                if (inside)
                    return length;
            }
        }
        var shortest = _spaces.Min(s => s.Low.Length);
        return Math.Min(shortest, text.Length);
    }
}

/// <summary>A Type 3 font: glyphs drawn by content streams.</summary>
internal sealed class Type3Font : PsFont
{
    private const int MaxGlyphDepth = 4;

    private readonly string _name;
    private readonly PsFontSet _set;
    private readonly int _depth;
    private readonly double[] _matrix;
    private readonly double[] _bbox;
    private readonly string?[] _names;
    private readonly double[] _widths = new double[256];
    private readonly PdfDictionary? _procs;
    private readonly PdfDictionary? _resources;
    private readonly Dictionary<int, string> _glyphs = [];

    public Type3Font(PdfDictionary font, string name, PsFontSet set, int depth)
    {
        if (depth > MaxGlyphDepth)
            throw new PsUntranslatableException("Type 3 glyphs draw Type 3 glyphs too deeply.");
        _name = name;
        _set = set;
        _depth = depth;
        _matrix = PdfObjects.Numbers(font, "/FontMatrix") is { Length: 6 } m && Math.Abs(m[0] * m[3] - m[1] * m[2]) > 1e-12 ? m : [0.001, 0, 0, 0.001, 0, 0];
        _bbox = PdfObjects.Numbers(font, "/FontBBox") is { Length: 4 } b ? b : [0, 0, 0, 0];
        _names = SimpleEncodings.Resolve(font, new string?[256], out _);
        _procs = PdfObjects.Dictionary(font, "/CharProcs");
        _resources = PdfObjects.Dictionary(font, "/Resources");
        var widths = SimpleWidths.Read(font, _matrix[0]);
        for (var code = 0; code < 256; code++)
            _widths[code] = widths[code] ?? 0;
    }

    public override bool IsType3 => true;

    public override void Show(ReadOnlySpan<byte> text, List<ShownGlyph> glyphs)
    {
        foreach (var b in text)
        {
            int code = b;
            if (!_glyphs.ContainsKey(code))
            {
                _set.CountGlyph();
                _glyphs[code] = _names[code] is { } glyphName && PdfObjects.Dictionary(_procs, "/" + glyphName) is { Stream: not null } proc
                    ? _set.TranslateGlyph(proc, _resources, _depth + 1)
                    : "0 0 setcharwidth";
            }
            glyphs.Add(new ShownGlyph(_name, b, _widths[code], code == 32));
        }
    }

    public override void Write(PsWriter w)
    {
        w.Line($"%%BeginResource: font {_name}");
        w.Line("12 dict begin");
        w.Line("/FontType 3 def");
        w.NewLine().Name("FontMatrix").Array(_matrix).Token("def").Name("FontBBox").Array(_bbox).Token("def");
        w.Line("/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for");
        foreach (var code in _glyphs.Keys.Order())
            w.Token($"dup {code} /a{code} put");
        w.Line("def");
        w.Line($"/CharProcs {_glyphs.Count + 1} dict dup begin /.notdef {{0 0 setcharwidth}} def");
        foreach (var (code, proc) in _glyphs.OrderBy(g => g.Key))
        {
            w.Line($"/a{code} {{FileeDict begin");
            w.Raw(proc);
            w.Line("end} bind def");
        }
        w.Line("end def");
        w.Line("/BuildGlyph {exch /CharProcs get exch 2 copy known not {pop /.notdef} if get exec} bind def");
        w.Line("/BuildChar {1 index /Encoding get exch get 1 index /BuildGlyph get exec} bind def");
        w.Line($"currentdict end /{_name} exch definefont pop");
        w.Line("%%EndResource");
        Store(w, _name);
    }
}
