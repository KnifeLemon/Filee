// Fonts a PDF draws with but doesn't embed (the standard 14 fonts, fonts the writer expected on the reader's computer),
// embedded for PDF/A (ISO 19005-2 6.2.11.4.1) from the fonts installed on this computer, for PdfAConformance.
//
// - Which font: the PDF's name for it (subset tag, PostScript suffixes and style words taken off, "TimesNewRoman" read
//   as "Times New Roman", the descriptor's FontFamily first) and its metric-compatible replacements (FontNames:
//   Helvetica → Arial → Liberation Sans, Times → Times New Roman → Liberation Serif, Courier → Courier New → Liberation
//   Mono), in the style the name and descriptor ask for. A face that would need a faked bold or italic isn't taken.
// - Whether it is the same font: the widths the PDF gives (Widths, MissingWidth, or the standard font's own metrics
//   for a standard font without Widths) are compared with the installed font's advances for the codes the pages draw
//   (PdfFontUses; every code of the encoding when Filee can't follow every use). A font whose glyphs are clearly not
//   the PDF's (more than one code in ten off by more than MaxNear, or any off by more than MaxFar) is passed over.
// - What is embedded: a glyph-ID-preserving TrueType subset (FontSubsetter, CFF outlines converted) of the glyphs
//   drawn, whose advances are set to the PDF's widths so the dictionary and the program agree (6.2.11.5).
//   Simple fonts (Type 1, MMType1, TrueType) become symbolic TrueType fonts without an Encoding and with a single (3,0)
//   cmap from 0xF000 + code, so each code draws the glyph its encoding named (6.2.11.6); a ToUnicode map from the
//   encoding's glyph names keeps the text. Composite fonts with an Identity encoding become CIDFontType2 with a
//   CIDToGIDMap: CIDs through the font's ToUnicode map to the installed font's glyphs, or, for a TrueType CIDFont found
//   by its own name, CIDs as glyph IDs (checked by the widths like any other).
// - When none is found: ConversionRefusedException with pdfa.reason.font_not_embedded and the font's name as detail,
//   so the job queue tries Ghostscript and the file says which font is missing. A font listed but never drawn (when
//   every use was followed) is left as it is: PDF/A asks only fonts used for rendering to be embedded.
//
// Predefined CMaps (UniJIS-UCS2-H …) and Type 3 fonts aren't handled here: the former are refused, the latter are
// drawings and need no program.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Fonts;
using Filee.Engines.Pdf.PostScript;
using Filee.Engines.Typesetting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace Filee.Engines.Pdf;

/// <summary>Embeds the fonts of a PDF that aren't embedded, from installed fonts with the same widths.</summary>
internal static partial class PdfAFonts
{
    /// <summary>Width difference (1/1000 em) still counted as the same glyph: rounding and font versions.</summary>
    internal const double MaxNear = 12;

    /// <summary>Width difference that shows another font's glyph.</summary>
    internal const double MaxFar = 150;

    private const int FixedPitch = 1, Serif = 2, Symbolic = 4, Script = 8, Nonsymbolic = 32, Italic = 64, ForceBold = 1 << 18;

    /// <summary>CIDs one composite font maps at most (two-byte codes).</summary>
    private const int MaxCids = 0x10000;

    private static PdfItem? Resolve(PdfItem? item) => PdfCompressor.Resolve(item);

    private static string Name(PdfDictionary? dictionary, string key) =>
        dictionary is not null && Resolve(dictionary.Elements[key]) is PdfName name ? name.Value : "";

    private static int Integer(PdfDictionary? dictionary, string key) => (int)(PdfObjects.Number(dictionary, key, 0));

    /// <summary>Whether the font (or each CIDFont of a Type 0 font) has its program in the file.</summary>
    public static bool IsEmbedded(PdfDictionary font)
    {
        switch (Name(font, "/Subtype"))
        {
            case "/Type3":
                return true;
            case "/Type0":
                var descendants = Descendants(font);
                return descendants.Count > 0 && descendants.All(HasProgram);
            default:
                return HasProgram(font);
        }
    }

    private static List<PdfDictionary> Descendants(PdfDictionary font) =>
        Resolve(font.Elements["/DescendantFonts"]) is PdfArray array ? [.. array.Elements.Select(Resolve).OfType<PdfDictionary>()] : [];

    private static bool HasProgram(PdfDictionary font) =>
        Resolve(font.Elements["/FontDescriptor"]) is PdfDictionary descriptor
        && new[] { "/FontFile", "/FontFile2", "/FontFile3" }.Any(key => Resolve(descriptor.Elements[key]) is PdfDictionary { Stream: not null });

    /// <summary>The font's name without a subset tag (for messages and lookups).</summary>
    public static string BaseName(PdfDictionary font)
    {
        var name = Name(font, "/BaseFont").TrimStart('/');
        if (name.Length == 0 && Name(font, "/Subtype") == "/Type0" && Descendants(font) is [var first, ..])
            name = Name(first, "/BaseFont").TrimStart('/');
        return name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper) ? name[7..] : name;
    }

    /// <summary>
    /// Embeds <paramref name="fonts"/> (none of them embedded) from <paramref name="installed"/>.
    /// </summary>
    /// <exception cref="ConversionRefusedException">A font drawn with has no installed match
    /// (<see cref="PdfAConformance.FontNotEmbedded"/>, its name as the detail).</exception>
    public static void Embed(PdfDocument document, IReadOnlyCollection<PdfDictionary> fonts, IFontCollection installed, CancellationToken cancellationToken)
    {
        if (fonts.Count == 0)
            return;
        var uses = PdfFontUses.Find(document, cancellationToken);
        foreach (var font in fonts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<int>? codes = uses.Unsure.Contains(font) ? null : uses.Codes.GetValueOrDefault(font) ?? [];
            bool embedded;
            try
            {
                embedded = Name(font, "/Subtype") == "/Type0"
                    ? EmbedComposite(document, font, codes, installed, cancellationToken)
                    : EmbedSimple(document, font, codes, installed, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                embedded = false;
            }
            // Not drawn at all: PDF/A doesn't ask for its program.
            if (!embedded && codes is not { Count: 0 })
                throw new ConversionRefusedException(PdfAConformance.FontNotEmbedded, BaseName(font));
        }
    }

    // ---- which installed font

    /// <summary>Families to look the font up by, best first, and the style it asks for.</summary>
    internal static (List<string> Families, FontStyle Style) Describe(string baseFont, PdfDictionary? descriptor)
    {
        var flags = Integer(descriptor, "/Flags");
        var cut = baseFont.IndexOfAny([',', '-']);
        var family = cut < 0 ? baseFont : baseFont[..cut];
        var rest = cut < 0 ? "" : baseFont[(cut + 1)..];
        foreach (var suffix in (string[])["PSMT", "MT", "PS"])
        {
            if (family.Length > suffix.Length + 2 && family.EndsWith(suffix, StringComparison.Ordinal))
            {
                family = family[..^suffix.Length];
                break;
            }
        }
        // "ArialBold", "TimesNewRomanItalic": style words run into the family.
        var styled = StyleSuffix().Match(family);
        if (styled.Success && styled.Index > 2)
        {
            rest = styled.Value + rest;
            family = family[..styled.Index];
        }
        var families = new List<string>();
        if (descriptor is not null && Resolve(descriptor.Elements["/FontFamily"]) is PdfString { Value.Length: > 0 } named)
            families.Add(named.Value);
        families.Add(CamelCase().Replace(family, " "));
        families.Add(family);

        var weight = 400;
        if (rest.Contains("Black", StringComparison.OrdinalIgnoreCase) || rest.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
            weight = 900;
        else if (rest.Contains("Semibold", StringComparison.OrdinalIgnoreCase) || rest.Contains("Demi", StringComparison.OrdinalIgnoreCase))
            weight = 600;
        else if (rest.Contains("Bold", StringComparison.OrdinalIgnoreCase) || (flags & ForceBold) != 0 || PdfObjects.Number(descriptor, "/FontWeight", 400) >= 600)
            weight = 700;
        else if (rest.Contains("Medium", StringComparison.OrdinalIgnoreCase))
            weight = 500;
        else if (rest.Contains("Light", StringComparison.OrdinalIgnoreCase))
            weight = 300;
        var italic = rest.Contains("Italic", StringComparison.OrdinalIgnoreCase) || rest.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
            || (flags & Italic) != 0 || PdfObjects.Number(descriptor, "/ItalicAngle", 0) != 0;
        return ([.. families.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)], new FontStyle(weight, italic));
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCase();

    [GeneratedRegex("(Bold|Italic|Oblique|BoldItalic|BoldOblique|Regular|Roman)+$", RegexOptions.CultureInvariant)]
    private static partial Regex StyleSuffix();

    /// <summary>Installed faces that may be the font, best first; Exact when found by the font's own name.</summary>
    private static IEnumerable<(FontFace Face, bool Exact)> Faces(IFontCollection installed, string baseFont, PdfDictionary? descriptor)
    {
        var (families, style) = Describe(baseFont, descriptor);
        var seen = new HashSet<FontFace>(ReferenceEqualityComparer.Instance);
        foreach (var family in families)
        {
            var exact = true;
            foreach (var name in FontNames.Alternatives(family).Prepend(family))
            {
                var faces = installed.Family(name);
                if (faces.Count > 0)
                {
                    var match = FontResolver.Match(faces, style);
                    if (!match.SyntheticBold && !match.SyntheticItalic && !match.Face.Cff2 && seen.Add(match.Face))
                        yield return (match.Face, exact);
                }
                exact = false;
            }
        }
    }

    /// <summary>An installed font's tables, cmaps and advances.</summary>
    private sealed class Program
    {
        private readonly byte[] _hmtx;
        private readonly int _metrics;

        private Program(SfntFont sfnt)
        {
            Sfnt = sfnt;
            UnitsPerEm = FontTables.UnitsPerEm(sfnt.Require("head"));
            GlyphCount = sfnt.GlyphCount;
            _hmtx = sfnt.Require("hmtx");
            _metrics = FontTables.NumberOfMetrics(sfnt.Require("hhea"));
            if (UnitsPerEm == 0 || _metrics == 0)
                throw new InvalidDataException("The font's metrics are damaged.");
            foreach (var (platform, encoding, map) in sfnt["cmap"] is { } cmap ? CmapTable.ReadAll(cmap) : [])
            {
                switch ((platform, encoding))
                {
                    case (3, 10) or (0, 4) or (0, 6):
                        Unicode = map;
                        break;
                    case (3, 1) or (0, 3) or (0, 2) or (0, 1) or (0, 0):
                        Unicode ??= map;
                        break;
                    case (3, 0):
                        Symbol = map;
                        break;
                }
            }
        }

        public SfntFont Sfnt { get; }
        public int UnitsPerEm { get; }
        public int GlyphCount { get; }
        public Dictionary<int, int>? Unicode { get; }
        public Dictionary<int, int>? Symbol { get; }

        public static Program? Open(FontFace face)
        {
            try
            {
                var sfnt = face.Font.Sfnt;
                return FontSubsetter.CanSubset(sfnt, out _) ? new Program(sfnt) : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException)
            {
                return null;
            }
        }

        /// <summary>A glyph's advance in 1/1000 em.</summary>
        public double Advance(int glyph)
        {
            var index = Math.Min(glyph, _metrics - 1);
            if (index * 4 + 2 > _hmtx.Length)
                return 0;
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(_hmtx.AsSpan(index * 4)) * 1000.0 / UnitsPerEm;
        }

        public int? ForUnicode(int codePoint) => Unicode is not null && Unicode.TryGetValue(codePoint, out var glyph) && glyph > 0 ? glyph : null;

        public int? ForSymbolCode(int code)
        {
            if (Symbol is null)
                return null;
            foreach (var key in (int[])[0xF000 + code, code, 0xF100 + code, 0xF200 + code])
            {
                if (Symbol.TryGetValue(key, out var glyph) && glyph > 0)
                    return glyph;
            }
            return null;
        }
    }

    /// <summary>Codes (or CIDs) matched to an installed font's glyphs with the widths the PDF gives them.</summary>
    private sealed class Match(Program program)
    {
        public Program Program { get; } = program;
        public SortedDictionary<int, int> Glyphs { get; } = [];
        public Dictionary<int, double> Widths { get; } = [];
        private int _compared;
        private int _off;

        /// <summary>
        /// Takes code → glyph with the PDF's width (null: the font's own); false when the glyph's width shows another
        /// font, or one glyph would need two widths.
        /// </summary>
        public bool Add(int code, int glyph, double? width)
        {
            var natural = Program.Advance(glyph);
            var wanted = width ?? natural;
            if (width is { } w)
            {
                var difference = Math.Abs(w - natural);
                _compared++;
                if (difference > MaxFar)
                    return false;
                if (difference > Math.Max(MaxNear, w * 0.03))
                    _off++;
            }
            if (Widths.TryGetValue(glyph, out var earlier) && Math.Abs(earlier - wanted) > 1)
                return false;
            Widths[glyph] = wanted;
            Glyphs[code] = glyph;
            return true;
        }

        /// <summary>Whether the widths compared say this is the PDF's font.</summary>
        public bool Same => _off <= Math.Max(1, _compared / 10) && (_compared < 4 || _off * 4 < _compared);
    }

    // ---- simple fonts

    private static bool EmbedSimple(PdfDocument document, PdfDictionary font, HashSet<int>? used, IFontCollection installed, CancellationToken cancellationToken)
    {
        var descriptor = Resolve(font.Elements["/FontDescriptor"]) as PdfDictionary;
        var flags = Integer(descriptor, "/Flags");
        var baseFont = BaseName(font);
        var standard = StandardName(baseFont);
        var symbolic = (flags & Symbolic) != 0 || standard is "Symbol" or "ZapfDingbats";
        string?[]? builtIn = standard is "Symbol" or "ZapfDingbats" ? SimpleEncodings.Named(standard) : null;
        var names = SimpleEncodings.Resolve(font, builtIn, out _, symbolic);
        var widths = SimpleWidthsOf(font, descriptor, names, standard);
        var known = used is not null;
        var codes = used?.Where(c => c is >= 0 and < 256).ToHashSet()
            ?? [.. Enumerable.Range(0, 256).Where(c => names[c] is { } n && n != ".notdef" || widths[c] is > 0)];

        foreach (var (face, _) in Faces(installed, baseFont, descriptor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Program.Open(face) is not { } program)
                continue;
            var match = new Match(program);
            var fits = true;
            foreach (var code in codes.Order())
            {
                var byName = names[code] is { } glyphName && glyphName != ".notdef" && SimpleEncodings.Unicode(glyphName) is var u and >= 0
                    ? program.ForUnicode(u) : null;
                var glyph = symbolic ? program.ForSymbolCode(code) ?? byName : byName ?? program.ForSymbolCode(code);
                if (glyph is not { } g)
                {
                    // A drawn code the font lacks would show its missing glyph (6.2.11.8).
                    if (known)
                    {
                        fits = false;
                        break;
                    }
                    continue;
                }
                if (!match.Add(code, g, widths[code]))
                {
                    fits = false;
                    break;
                }
            }
            if (!fits || !match.Same || (match.Glyphs.Count == 0 && codes.Count > 0))
                continue;
            WriteSimple(document, font, descriptor, flags, names, widths, match, cancellationToken);
            return true;
        }
        return false;
    }

    /// <summary>The standard 14 font a name stands for ("Arial,Bold" → Helvetica-Bold), or null.</summary>
    private static string? StandardName(string baseFont)
    {
        var compact = baseFont.Replace(" ", "", StringComparison.Ordinal);
        return Standard14.GetMappedFontName(compact) is { Length: > 0 } mapped && Standard14.IsFontInStandard14(mapped) ? mapped : null;
    }

    /// <summary>The width of every code the PDF gives: Widths, else the standard font's metrics, else MissingWidth.</summary>
    private static double?[] SimpleWidthsOf(PdfDictionary font, PdfDictionary? descriptor, string?[] names, string? standard)
    {
        var widths = new double?[256];
        var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
        var listed = new bool[256];
        var array = PdfObjects.Array(font, "/Widths");
        if (array is not null)
        {
            for (var i = 0; i < array.Elements.Count && first + i < 256; i++)
            {
                if (first + i >= 0 && PdfObjects.Number(array.Elements[i]) is { } w)
                {
                    widths[first + i] = w;
                    listed[first + i] = true;
                }
            }
        }
        var metrics = standard is null ? null : Standard14.GetAdobeFontMetrics(standard);
        var missing = PdfObjects.Number(PdfObjects.Get(descriptor, "/MissingWidth"));
        for (var code = 0; code < 256; code++)
        {
            if (listed[code])
                continue;
            if (metrics is not null && names[code] is { } name && metrics.CharacterMetrics.TryGetValue(name, out var metric))
                widths[code] = metric.Width.X;
            else if (missing is { } m && array is not null)
                widths[code] = m;
        }
        return widths;
    }

    private static void WriteSimple(PdfDocument document, PdfDictionary font, PdfDictionary? old, int flags, string?[] names, double?[] widths,
        Match match, CancellationToken cancellationToken)
    {
        var program = match.Program;
        var cmap = match.Glyphs.ToDictionary(p => 0xF000 + p.Key, p => p.Value);
        var bytes = Subset(program, match, cmap, symbol: true, cancellationToken);
        var postScript = PostScriptName(program.Sfnt) ?? Compact(BaseName(font));
        var name = "/" + Tag(postScript, match.Glyphs.Values) + "+" + postScript;

        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements.SetName("/BaseFont", name);
        font.Elements.Remove("/Encoding"); // symbolic TrueType (6.2.11.6)
        var codes = match.Glyphs.Keys.ToList();
        var first = codes.Count > 0 ? codes[0] : 32;
        var last = codes.Count > 0 ? codes[^1] : 32;
        if (PdfObjects.Array(font, "/Widths") is not null)
        {
            first = Math.Min(first, Math.Clamp((int)PdfObjects.Number(font, "/FirstChar", first), 0, 255));
            last = Math.Max(last, Math.Clamp((int)PdfObjects.Number(font, "/LastChar", last), 0, 255));
        }
        var array = new PdfArray(document);
        for (var code = first; code <= last; code++)
        {
            var width = widths[code] ?? (match.Glyphs.TryGetValue(code, out var glyph) ? match.Widths[glyph] : 0);
            array.Elements.Add(Number(width));
        }
        font.Elements.SetInteger("/FirstChar", first);
        font.Elements.SetInteger("/LastChar", last);
        font.Elements["/Widths"] = array;
        font.Elements["/FontDescriptor"] = Descriptor(document, old, name, (flags | Symbolic) & ~Nonsymbolic, program, bytes).Reference;
        if (!font.Elements.ContainsKey("/ToUnicode"))
        {
            var text = new SortedDictionary<int, int>();
            foreach (var code in codes)
            {
                if (names[code] is { } glyphName && SimpleEncodings.Unicode(glyphName) is var u and > 0)
                    text[code] = u;
            }
            if (text.Count > 0)
                font.Elements["/ToUnicode"] = ToUnicode(document, text, 1).Reference;
        }
    }

    // ---- composite fonts

    private static bool EmbedComposite(PdfDocument document, PdfDictionary font, HashSet<int>? used, IFontCollection installed, CancellationToken cancellationToken)
    {
        if (Name(font, "/Encoding") is not ("/Identity-H" or "/Identity-V") || Descendants(font) is not [var cidFont, ..])
            return false; // a predefined CMap: its CIDs can't be told without the CMap file
        var descriptor = Resolve(cidFont.Elements["/FontDescriptor"]) as PdfDictionary;
        var trueType = Name(cidFont, "/Subtype") == "/CIDFontType2";
        var text = ReadToUnicode(Resolve(font.Elements["/ToUnicode"]) as PdfDictionary);
        var widths = CidWidths(cidFont);
        var standardWidth = PdfObjects.Number(cidFont, "/DW", 1000);
        var known = used is not null;
        var cids = used?.Where(c => c is >= 0 and < MaxCids).ToHashSet() ?? [.. text.Keys.Concat(widths.Keys)];

        foreach (var (face, exact) in Faces(installed, BaseName(cidFont), descriptor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Program.Open(face) is not { } program)
                continue;
            var match = new Match(program);
            var fits = true;
            foreach (var cid in cids.Order())
            {
                int? glyph = text.TryGetValue(cid, out var codePoint) ? program.ForUnicode(codePoint)
                    : exact && trueType && cid > 0 && cid < program.GlyphCount ? cid
                    : null;
                if (glyph is not { } g)
                {
                    if (known)
                    {
                        fits = false;
                        break;
                    }
                    continue;
                }
                if (!match.Add(cid, g, widths.TryGetValue(cid, out var w) ? w : standardWidth))
                {
                    fits = false;
                    break;
                }
            }
            if (!fits || !match.Same || (match.Glyphs.Count == 0 && cids.Count > 0))
                continue;
            WriteComposite(document, font, cidFont, descriptor, text, match, cancellationToken);
            return true;
        }
        return false;
    }

    private static void WriteComposite(PdfDocument document, PdfDictionary font, PdfDictionary cidFont, PdfDictionary? old,
        Dictionary<int, int> text, Match match, CancellationToken cancellationToken)
    {
        var program = match.Program;
        var bytes = Subset(program, match, null, symbol: false, cancellationToken);
        var postScript = PostScriptName(program.Sfnt) ?? Compact(BaseName(cidFont));
        var name = "/" + Tag(postScript, match.Glyphs.Values) + "+" + postScript;
        cidFont.Elements.SetName("/Subtype", "/CIDFontType2");
        cidFont.Elements.SetName("/BaseFont", name);
        font.Elements.SetName("/BaseFont", name);
        if (match.Glyphs.All(p => p.Key == p.Value))
        {
            cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        }
        else
        {
            var map = new byte[2 * (match.Glyphs.Keys.Max() + 1)];
            foreach (var (cid, glyph) in match.Glyphs)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(2 * cid), (ushort)glyph);
            var stream = new PdfDictionary(document);
            stream.CreateStream(map);
            stream.Stream.Zip();
            document.Internals.AddObject(stream);
            cidFont.Elements["/CIDToGIDMap"] = stream.Reference;
        }
        var flags = Integer(old, "/Flags");
        cidFont.Elements["/FontDescriptor"] = Descriptor(document, old, name, (flags | Symbolic) & ~Nonsymbolic, program, bytes).Reference;
        if (!font.Elements.ContainsKey("/ToUnicode") && program.Unicode is { } unicode)
        {
            // CIDs read as glyph IDs: their text from the font's own cmap.
            var byGlyph = new Dictionary<int, int>();
            foreach (var (codePoint, glyph) in unicode.OrderByDescending(p => p.Key))
                byGlyph[glyph] = codePoint;
            var map = new SortedDictionary<int, int>();
            foreach (var (cid, glyph) in match.Glyphs)
            {
                if (text.TryGetValue(cid, out var codePoint) || byGlyph.TryGetValue(glyph, out codePoint))
                    map[cid] = codePoint;
            }
            if (map.Count > 0)
                font.Elements["/ToUnicode"] = ToUnicode(document, map, 2).Reference;
        }
    }

    /// <summary>A CIDFont's widths (W), CID → width, bounded.</summary>
    private static Dictionary<int, double> CidWidths(PdfDictionary cidFont)
    {
        var widths = new Dictionary<int, double>();
        if (PdfObjects.Array(cidFont, "/W") is not { } array)
            return widths;
        var items = array.Elements.Select(Resolve).ToList();
        for (var i = 0; i < items.Count && widths.Count < MaxCids;)
        {
            if (PdfObjects.Number(items[i]) is not { } start)
                break;
            if (i + 1 < items.Count && items[i + 1] is PdfArray list)
            {
                var cid = (int)start;
                foreach (var w in list.Elements.Select(PdfObjects.Number))
                {
                    if (cid >= 0 && cid < MaxCids && w is { } width)
                        widths[cid] = width;
                    cid++;
                }
                i += 2;
            }
            else if (i + 2 < items.Count && PdfObjects.Number(items[i + 1]) is { } end && PdfObjects.Number(items[i + 2]) is { } width)
            {
                for (var cid = Math.Max(0, (int)start); cid <= Math.Min(MaxCids - 1, (int)end) && widths.Count < MaxCids; cid++)
                    widths[cid] = width;
                i += 3;
            }
            else
            {
                break;
            }
        }
        return widths;
    }

    /// <summary>
    /// The single characters of a ToUnicode CMap (bfchar and bfrange), code → code point; empty when there is none or
    /// it can't be read.
    /// </summary>
    internal static Dictionary<int, int> ReadToUnicode(PdfDictionary? stream)
    {
        var map = new Dictionary<int, int>();
        if (stream?.Stream is null)
            return map;
        try
        {
            var data = stream.Stream.UnfilteredValue;
            if (data.Length > PdfFontUses.MaxStreamSize * 8L)
                return map;
            var lexer = new PdfContentLexer(data);
            var operands = new List<object?>();
            string? section = null;
            while (lexer.Next() is { } token && map.Count < MaxCids)
            {
                if (token is not PdfContentLexer.Operator op)
                {
                    operands.Add(token);
                    continue;
                }
                switch (op.Name)
                {
                    case "beginbfchar" or "beginbfrange":
                        section = op.Name;
                        break;
                    case "endbfchar":
                        for (var i = 0; i + 1 < operands.Count; i += 2)
                        {
                            if (operands[i] is byte[] code && Single(operands[i + 1]) is { } c)
                                map[Code(code)] = c;
                        }
                        section = null;
                        break;
                    case "endbfrange":
                        for (var i = 0; i + 2 < operands.Count; i += 3)
                        {
                            if (operands[i] is not byte[] low || operands[i + 1] is not byte[] high)
                                continue;
                            int from = Code(low), to = Math.Min(Code(high), from + MaxCids);
                            if (operands[i + 2] is List<object?> each)
                            {
                                for (var code = from; code <= to && code - from < each.Count; code++)
                                {
                                    if (Single(each[code - from]) is { } c)
                                        map[code] = c;
                                }
                            }
                            else if (Single(operands[i + 2]) is { } c)
                            {
                                for (var code = from; code <= to && map.Count < MaxCids; code++)
                                    map[code] = c + code - from;
                            }
                        }
                        section = null;
                        break;
                }
                if (section is null || op.Name.StartsWith("begin", StringComparison.Ordinal))
                    operands.Clear();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            map.Clear();
        }
        return map;

        static int Code(byte[] bytes) => bytes.Take(4).Aggregate(0, (value, b) => (value << 8) | b);

        static int? Single(object? item)
        {
            if (item is not byte[] { Length: 2 or 4 } bytes)
                return null;
            var text = Encoding.BigEndianUnicode.GetString(bytes);
            return text.EnumerateRunes().Take(2).ToList() is [var rune] && rune.Value > 0 ? rune.Value : null;
        }
    }

    // ---- writing

    /// <summary>The subset of the matched glyphs, advances set to the PDF's widths.</summary>
    private static byte[] Subset(Program program, Match match, Dictionary<int, int>? cmap, bool symbol, CancellationToken cancellationToken)
    {
        var subset = FontSubsetter.Subset(program.Sfnt, match.Glyphs.Values, cancellationToken);
        var hhea = (byte[])subset.Require("hhea").Clone();
        var hmtx = (byte[])subset.Require("hmtx").Clone();
        var metrics = FontTables.NumberOfMetrics(hhea);
        var widest = 0;
        for (var glyph = 0; glyph < metrics; glyph++)
        {
            var advance = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(glyph * 4));
            if (match.Widths.TryGetValue(glyph, out var width))
            {
                advance = (ushort)Math.Clamp(Math.Round(width * program.UnitsPerEm / 1000), 0, ushort.MaxValue);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(glyph * 4), advance);
            }
            widest = Math.Max(widest, advance);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), (ushort)widest);
        subset.Tables["hhea"] = hhea;
        subset.Tables["hmtx"] = hmtx;
        if (cmap is not null)
            subset.Tables["cmap"] = CmapTable.Build(cmap, symbol);
        return subset.Write();
    }

    /// <summary>A new font descriptor for the embedded program (the old one may be shared), with its FontFile2.</summary>
    private static PdfDictionary Descriptor(PdfDocument document, PdfDictionary? old, string name, int flags, Program program, byte[] bytes)
    {
        var sfnt = program.Sfnt;
        double Scale(int value) => Math.Round(value * 1000.0 / program.UnitsPerEm);
        var head = sfnt.Require("head");
        var hhea = sfnt.Require("hhea");
        short I16(byte[] table, int at) => table.Length >= at + 2 ? System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(at)) : (short)0;
        var descriptor = new PdfDictionary(document);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", name);
        descriptor.Elements.SetInteger("/Flags", flags & (FixedPitch | Serif | Symbolic | Script | Italic | ForceBold | (1 << 16) | (1 << 17)));
        var box = new PdfArray(document);
        foreach (var at in new[] { 36, 38, 40, 42 })
            box.Elements.Add(Number(Scale(I16(head, at))));
        descriptor.Elements["/FontBBox"] = box;
        var italicAngle = sfnt["post"] is { Length: >= 8 } post ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(post.AsSpan(4)) / 65536.0 : 0;
        descriptor.Elements["/ItalicAngle"] = Number(Math.Round(italicAngle, 2));
        var ascent = Scale(I16(hhea, 4));
        descriptor.Elements["/Ascent"] = Number(ascent);
        descriptor.Elements["/Descent"] = Number(Scale(I16(hhea, 6)));
        var os2 = sfnt["OS/2"];
        var capHeight = os2 is { Length: >= 90 } && I16(os2, 0) >= 2 ? Scale(I16(os2, 88)) : ascent;
        descriptor.Elements["/CapHeight"] = Number(capHeight);
        var weight = os2 is { Length: >= 6 } ? I16(os2, 4) : 400;
        descriptor.Elements["/StemV"] = Number(old is not null && PdfObjects.Number(PdfObjects.Get(old, "/StemV")) is { } stem ? stem : 50 + weight / 8);
        if (old is not null && PdfObjects.Number(PdfObjects.Get(old, "/MissingWidth")) is { } missing)
            descriptor.Elements["/MissingWidth"] = Number(missing);

        var file = new PdfDictionary(document);
        file.CreateStream(bytes);
        file.Elements.SetInteger("/Length1", bytes.Length);
        file.Stream.Zip();
        document.Internals.AddObject(file);
        descriptor.Elements["/FontFile2"] = file.Reference;
        document.Internals.AddObject(descriptor);
        return descriptor;
    }

    /// <summary>A ToUnicode CMap of <paramref name="bytes"/>-byte codes.</summary>
    private static PdfDictionary ToUnicode(PdfDocument document, SortedDictionary<int, int> map, int bytes)
    {
        var digits = bytes * 2;
        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append(CultureInfo.InvariantCulture, $"1 begincodespacerange\n<{new string('0', digits)}> <{new string('F', digits)}>\nendcodespacerange\n");
        foreach (var chunk in map.Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var (code, codePoint) in chunk)
            {
                var utf16 = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(codePoint)));
                cmap.Append('<').Append(code.ToString("X" + digits, CultureInfo.InvariantCulture)).Append("> <").Append(utf16).Append(">\n");
            }
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.ASCII.GetBytes(cmap.ToString()));
        stream.Stream.Zip();
        document.Internals.AddObject(stream);
        return stream;
    }

    private static PdfItem Number(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 ? new PdfInteger((int)Math.Round(value)) : new PdfReal(Math.Round(value, 3));

    /// <summary>The font's PostScript name (name ID 6), made safe for a PDF name; null without one.</summary>
    private static string? PostScriptName(SfntFont sfnt)
    {
        if (sfnt["name"] is not { Length: >= 6 } table)
            return null;
        var r = new BigEndianReader(table);
        r.Skip(2);
        int count = r.U16(), storage = r.U16();
        for (var i = 0; i < count && r.Remaining >= 12; i++)
        {
            int platform = r.U16(), encoding = r.U16(), _ = r.U16(), id = r.U16(), length = r.U16(), offset = r.U16();
            if (id != 6 || storage + offset + length > table.Length)
                continue;
            var bytes = table.AsSpan(storage + offset, length);
            var text = platform == 1 && encoding == 0 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            if (Compact(text) is { Length: > 0 } safe)
                return safe;
        }
        return null;
    }

    /// <summary>Printable ASCII without spaces and PDF delimiters, at most 63 characters.</summary>
    private static string Compact(string text)
    {
        var kept = new string([.. text.Where(c => c is > ' ' and < (char)127 and not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%' or '#'))]);
        return kept.Length > 63 ? kept[..63] : kept;
    }

    /// <summary>Six capital letters from the font and its glyphs, as subset tags are.</summary>
    private static string Tag(string name, IEnumerable<int> glyphs)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(name + ":" + string.Join(',', glyphs.Order())));
        return new string([.. hash.Take(6).Select(b => (char)('A' + b % 26))]);
    }
}
