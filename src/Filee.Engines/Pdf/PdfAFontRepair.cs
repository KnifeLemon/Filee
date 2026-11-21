// TrueType fonts the PDF embedded itself, repaired for PDF/A-2b where their dictionary and program disagree (part of
// PdfAFonts, run by PdfAConformance after the missing fonts are embedded):
//
// - 6.2.11.6 encodings: a symbolic font with an Encoding or without a (3,0) cmap, or a non-symbolic one whose encoding
//   isn't WinAnsi / MacRoman (or whose Differences need a (3,1) cmap it lacks), becomes a symbolic font without an
//   Encoding whose program has a single (3,0) cmap from 0xF000 + code to the glyph the code drew so far (by its glyph
//   name through the Unicode cmap, else by the code through the symbol or Macintosh cmap).
// - 6.2.11.5 widths: where the Widths (simple fonts) or W / DW (CIDFontType2 with an Identity encoding) of a drawn code
//   differ from the program's advance by more than 1/1000 em, the program's advance is set to the dictionary's width
//   (hmtx written with one advance per glyph), so the text keeps the places the PDF gives it. A glyph two codes draw
//   with different widths keeps one, and the dictionary takes the program's advance for the other.
// - ToUnicode: a font without one gets it from its encoding's glyph names, else from the program's Unicode cmap (the
//   characters of each glyph), so the text can be found and copied.
//
// A program that can't be read is left as it is (the PDF/A then shows the problem as before). Glyphs a program lacks
// (6.2.11.8) aren't added.

using System.Buffers.Binary;
using Filee.Engines.Fonts;
using Filee.Engines.Pdf.PostScript;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

internal static partial class PdfAFonts
{
    /// <summary>Width difference (1/1000 em) PDF/A's width rule allows between dictionary and program.</summary>
    private const double WidthSlack = 1;

    /// <summary>Repairs the embedded TrueType fonts among <paramref name="fonts"/> (see the file's notes).</summary>
    public static void Repair(PdfDocument document, IReadOnlyCollection<PdfDictionary> fonts, PdfFontUses uses, CancellationToken cancellationToken)
    {
        foreach (var font in fonts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<int>? codes = uses.Unsure.Contains(font) ? null : uses.Codes.GetValueOrDefault(font) ?? [];
            try
            {
                switch (Name(font, "/Subtype"))
                {
                    case "/TrueType":
                        RepairSimple(document, font, codes);
                        break;
                    case "/Type0" when Name(font, "/Encoding") is "/Identity-H" or "/Identity-V" && Descendants(font) is [var cidFont, ..]
                                       && Name(cidFont, "/Subtype") == "/CIDFontType2":
                        RepairComposite(document, font, cidFont, codes);
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                // A program Filee can't read or write: left as the PDF has it.
            }
        }
    }

    /// <summary>The TrueType program of a font's descriptor, read; null when it has none or it is damaged.</summary>
    private static (PdfDictionary Descriptor, SfntFont Program)? TrueTypeProgram(PdfDictionary font)
    {
        if (Resolve(font.Elements["/FontDescriptor"]) is not PdfDictionary descriptor
            || Resolve(descriptor.Elements["/FontFile2"]) is not PdfDictionary { Stream: not null } file)
            return null;
        var bytes = file.Stream.UnfilteredValue;
        if (bytes.Length == 0 || bytes.Length > PdfFontUses.MaxStreamSize * 8L)
            return null;
        var sfnt = SfntFont.Read(bytes);
        _ = sfnt.Require("head");
        _ = sfnt.Require("hhea");
        _ = sfnt.Require("hmtx");
        _ = sfnt.Require("maxp");
        return (descriptor, sfnt);
    }

    // ---- simple TrueType fonts

    private static void RepairSimple(PdfDocument document, PdfDictionary font, HashSet<int>? used)
    {
        if (TrueTypeProgram(font) is not var (descriptor, sfnt))
            return;
        var flags = Integer(descriptor, "/Flags");
        var symbolic = (flags & Symbolic) != 0;
        var cmaps = sfnt["cmap"] is { } table ? CmapTable.ReadAll(table) : [];
        Dictionary<int, int>? Cmap(int platform, int encoding) => cmaps.FirstOrDefault(c => c.Platform == platform && c.Encoding == encoding).Map;
        var names = font.Elements.ContainsKey("/Encoding") || !symbolic ? SimpleEncodings.Resolve(font, null, out _, symbolic: false) : new string?[256];
        var mac = SimpleEncodings.Named("MacRomanEncoding");

        int? BySymbol(int code)
        {
            foreach (var key in (int[])[code, 0xF000 + code, 0xF100 + code, 0xF200 + code])
            {
                if (Cmap(3, 0)?.TryGetValue(key, out var glyph) == true && glyph > 0)
                    return glyph;
            }
            return Cmap(1, 0)?.TryGetValue(code, out var g) == true && g > 0 ? g : null;
        }
        int? ByName(int code)
        {
            if (names[code] is not { } name || name == ".notdef")
                return null;
            if (SimpleEncodings.Unicode(name) is var u and > 0 && (Cmap(3, 1) ?? Cmap(3, 10) ?? Cmap(0, 3))?.TryGetValue(u, out var glyph) == true && glyph > 0)
                return glyph;
            var macCode = Array.IndexOf(mac, name);
            return macCode >= 0 && Cmap(1, 0)?.TryGetValue(macCode, out var g) == true && g > 0 ? g : null;
        }
        int? Drawn(int code) => symbolic ? BySymbol(code) ?? ByName(code) : ByName(code) ?? BySymbol(code);

        bool encodingKept;
        if (symbolic)
        {
            encodingKept = !font.Elements.ContainsKey("/Encoding") && (cmaps.Count == 1 || Cmap(3, 0) is not null);
        }
        else
        {
            var encoding = Resolve(font.Elements["/Encoding"]);
            var baseEncoding = encoding is PdfDictionary e ? Name(e, "/BaseEncoding") : (encoding as PdfName)?.Value;
            var differences = encoding is PdfDictionary d && Resolve(d.Elements["/Differences"]) is PdfArray { Elements.Count: > 0 } list ? list : null;
            encodingKept = baseEncoding is "/WinAnsiEncoding" or "/MacRomanEncoding"
                           && (differences is null || (Cmap(3, 1) is not null
                                                       && differences.Elements.Select(Resolve).OfType<PdfName>().All(n => SimpleEncodings.Unicode(n.Value.TrimStart('/')) >= 0)))
                           && !cmaps.All(c => (c.Platform, c.Encoding) == (3, 0));
        }

        // The codes drawn; when Filee can't follow every use, the codes the Widths array lists.
        var firstListed = Math.Clamp((int)PdfObjects.Number(font, "/FirstChar", 0), 0, 255);
        var lastListed = Math.Clamp((int)PdfObjects.Number(font, "/LastChar", 255), firstListed, 255);
        var glyphs = new SortedDictionary<int, int>();
        foreach (var code in used?.Where(c => c is >= 0 and < 256) ?? Enumerable.Range(firstListed, lastListed - firstListed + 1))
        {
            if (Drawn(code) is { } glyph && glyph < sfnt.GlyphCount)
                glyphs[code] = glyph;
        }
        var programChanged = false;
        var addText = !font.Elements.ContainsKey("/ToUnicode");
        var text = new SortedDictionary<int, int>();
        if (addText)
        {
            var reverse = Reverse(Cmap(3, 1) ?? Cmap(3, 10) ?? Cmap(0, 3));
            for (var code = 0; code < 256; code++)
            {
                if (names[code] is { } name && SimpleEncodings.Unicode(name) is var u and > 0)
                    text[code] = u;
                else if (Drawn(code) is { } glyph && reverse.TryGetValue(glyph, out var c))
                    text[code] = c;
            }
        }
        if (!encodingKept)
        {
            // Every code that draws something keeps its glyph through a (3,0) cmap of its own.
            var symbolMap = new Dictionary<int, int>();
            for (var code = 0; code < 256; code++)
            {
                if (Drawn(code) is { } glyph && glyph < sfnt.GlyphCount)
                    symbolMap[0xF000 + code] = glyph;
            }
            sfnt.Tables["cmap"] = CmapTable.Build(symbolMap, symbol: true);
            font.Elements.Remove("/Encoding");
            flags = (flags | Symbolic) & ~Nonsymbolic;
            programChanged = true;
        }

        // Widths: the program follows the dictionary; a glyph drawn with two widths keeps the first.
        var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
        var array = PdfObjects.Array(font, "/Widths");
        var missing = PdfObjects.Number(PdfObjects.Get(descriptor, "/MissingWidth")) ?? 0;
        double Width(int code) => array is not null && code - first >= 0 && code - first < array.Elements.Count
            && PdfObjects.Number(array.Elements[code - first]) is { } w ? w : missing;
        var wanted = glyphs.ToDictionary(p => p.Key, p => Width(p.Key));
        var (advancesChanged, keep) = FitAdvances(sfnt, glyphs, wanted);
        programChanged |= advancesChanged;
        if (keep.Count > 0)
        {
            // The dictionary takes the program's advance for codes whose glyph another code's width set.
            var codes = glyphs.Keys.ToList();
            var last = Math.Max((int)PdfObjects.Number(font, "/LastChar", first + (array?.Elements.Count ?? 0) - 1), codes.Max());
            first = Math.Min(first, codes.Min());
            var widths = new PdfArray(document);
            for (var code = first; code <= last; code++)
                widths.Elements.Add(Number(keep.TryGetValue(code, out var advance) ? Math.Round(advance, 3) : Width(code)));
            font.Elements.SetInteger("/FirstChar", first);
            font.Elements.SetInteger("/LastChar", last);
            font.Elements["/Widths"] = widths;
        }

        if (programChanged)
            ReplaceProgram(document, font, descriptor, sfnt, flags);
        if (addText && text.Count > 0)
            font.Elements["/ToUnicode"] = ToUnicode(document, text, 1).Reference;
    }

    /// <summary>The characters of each glyph in a Unicode cmap (the lowest code point when several share one).</summary>
    private static Dictionary<int, int> Reverse(Dictionary<int, int>? unicode)
    {
        var byGlyph = new Dictionary<int, int>();
        if (unicode is null)
            return byGlyph;
        foreach (var (codePoint, glyph) in unicode.OrderByDescending(p => p.Key))
        {
            if (glyph > 0 && codePoint > 0)
                byGlyph[glyph] = codePoint;
        }
        return byGlyph;
    }

    // ---- CIDFontType2 with an Identity encoding

    private static void RepairComposite(PdfDocument document, PdfDictionary font, PdfDictionary cidFont, HashSet<int>? used)
    {
        if (TrueTypeProgram(cidFont) is not var (descriptor, sfnt))
            return;
        var map = Resolve(cidFont.Elements["/CIDToGIDMap"]) is PdfDictionary { Stream: not null } stream ? stream.Stream.UnfilteredValue : null;
        int Glyph(int cid) => map is null ? cid : 2 * cid + 1 < map.Length ? BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(2 * cid)) : 0;
        var widths = CidWidths(cidFont);
        var standard = PdfObjects.Number(cidFont, "/DW", 1000);
        var glyphs = new SortedDictionary<int, int>();
        foreach (var cid in used?.Where(c => c is >= 0 and < MaxCids) ?? widths.Keys)
        {
            if (Glyph(cid) is var glyph and > 0 && glyph < sfnt.GlyphCount)
                glyphs[cid] = glyph;
        }
        var wanted = glyphs.ToDictionary(p => p.Key, p => widths.TryGetValue(p.Key, out var w) ? w : standard);
        var (advancesChanged, keep) = FitAdvances(sfnt, glyphs, wanted);
        if (keep.Count > 0)
        {
            foreach (var (cid, advance) in keep)
                widths[cid] = Math.Round(advance, 3);
            cidFont.Elements["/W"] = WidthArray(document, widths);
        }
        if (advancesChanged)
            ReplaceProgram(document, cidFont, descriptor, sfnt, Integer(descriptor, "/Flags"));
        if (!font.Elements.ContainsKey("/ToUnicode"))
        {
            var reverse = Reverse(sfnt["cmap"] is { } table
                ? CmapTable.ReadAll(table).Where(c => (c.Platform, c.Encoding) is (3, 1) or (3, 10) or (0, _)).OrderBy(c => c.Encoding == 10 ? 0 : 1).Select(c => c.Map).FirstOrDefault()
                : null);
            var text = new SortedDictionary<int, int>();
            foreach (var (cid, glyph) in glyphs)
            {
                if (reverse.TryGetValue(glyph, out var codePoint))
                    text[cid] = codePoint;
            }
            if (text.Count > 0)
                font.Elements["/ToUnicode"] = ToUnicode(document, text, 2).Reference;
        }
    }

    /// <summary>A CIDFont's W array of single CIDs in runs: "c [w1 w2 …]".</summary>
    private static PdfArray WidthArray(PdfDocument document, Dictionary<int, double> widths)
    {
        var array = new PdfArray(document);
        PdfArray? run = null;
        var next = -2;
        foreach (var (cid, width) in widths.OrderBy(p => p.Key))
        {
            if (cid != next || run is null)
            {
                array.Elements.Add(new PdfInteger(cid));
                run = new PdfArray(document);
                array.Elements.Add(run);
            }
            run.Elements.Add(Number(width));
            next = cid + 1;
        }
        return array;
    }

    // ---- the program

    /// <summary>
    /// Sets the advances of <paramref name="glyphs"/> (code → glyph) to <paramref name="wanted"/> (code → width,
    /// 1/1000 em) where they differ by more than the rule allows. Returns whether the program changed, and the codes
    /// whose dictionary width has to become the program's advance (a glyph another code set, or a width the font's
    /// units can't hold), with that advance.
    /// </summary>
    private static (bool Changed, Dictionary<int, double> Keep) FitAdvances(SfntFont sfnt, SortedDictionary<int, int> glyphs, Dictionary<int, double> wanted)
    {
        var keep = new Dictionary<int, double>();
        var unitsPerEm = FontTables.UnitsPerEm(sfnt.Require("head"));
        var hhea = sfnt.Require("hhea");
        var hmtx = sfnt.Require("hmtx");
        var metrics = FontTables.NumberOfMetrics(hhea);
        var count = sfnt.GlyphCount;
        if (unitsPerEm == 0 || metrics == 0 || hmtx.Length < metrics * 4 || hhea.Length < 36)
            throw new InvalidDataException("The font's metrics are damaged.");
        var advances = new int[count];
        var bearings = new short[count];
        for (var glyph = 0; glyph < count; glyph++)
        {
            var index = Math.Min(glyph, metrics - 1);
            advances[glyph] = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(index * 4));
            var at = glyph < metrics ? glyph * 4 + 2 : metrics * 4 + (glyph - metrics) * 2;
            bearings[glyph] = at + 2 <= hmtx.Length ? BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(at)) : (short)0;
        }
        double Shown(int advance) => advance * 1000.0 / unitsPerEm;
        var set = new Dictionary<int, double>();
        var changed = false;
        foreach (var (code, glyph) in glyphs)
        {
            var width = wanted[code];
            if (set.TryGetValue(glyph, out var earlier))
            {
                if (Math.Abs(earlier - width) > WidthSlack)
                    keep[code] = Shown(advances[glyph]);
                continue;
            }
            if (Math.Abs(width - Shown(advances[glyph])) <= WidthSlack)
            {
                set[glyph] = Shown(advances[glyph]);
                continue;
            }
            var units = Math.Round(width * unitsPerEm / 1000);
            if (units is < 0 or > ushort.MaxValue || Math.Abs(Shown((int)units) - width) > WidthSlack)
            {
                keep[code] = Shown(advances[glyph]);
                set[glyph] = Shown(advances[glyph]);
                continue;
            }
            advances[glyph] = (int)units;
            set[glyph] = width;
            changed = true;
        }
        if (!changed)
            return (false, keep);
        // One advance and bearing a glyph: every glyph can have its own.
        var table = new byte[count * 4];
        var widest = 0;
        for (var glyph = 0; glyph < count; glyph++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(glyph * 4), (ushort)advances[glyph]);
            BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(glyph * 4 + 2), bearings[glyph]);
            widest = Math.Max(widest, advances[glyph]);
        }
        var header = (byte[])hhea.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)widest);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34), (ushort)count);
        sfnt.Tables["hhea"] = header;
        sfnt.Tables["hmtx"] = table;
        return (true, keep);
    }

    /// <summary>The changed program as a new FontFile2 in a copy of the descriptor (the old one may be shared).</summary>
    private static void ReplaceProgram(PdfDocument document, PdfDictionary font, PdfDictionary descriptor, SfntFont sfnt, int flags)
    {
        var bytes = sfnt.Write();
        var file = new PdfDictionary(document);
        file.CreateStream(bytes);
        file.Elements.SetInteger("/Length1", bytes.Length);
        file.Stream.Zip();
        document.Internals.AddObject(file);
        var copy = new PdfDictionary(document);
        foreach (var key in descriptor.Elements.Keys)
            copy.Elements[key] = descriptor.Elements[key];
        copy.Elements.SetInteger("/Flags", flags);
        copy.Elements["/FontFile2"] = file.Reference;
        document.Internals.AddObject(copy);
        font.Elements["/FontDescriptor"] = copy.Reference;
    }
}
