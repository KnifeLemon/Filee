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
// - 6.2.11.4.1 / 6.2.11.8 missing glyphs: a code (or CID) drawn that maps to no glyph of the program draws .notdef,
//   which PDF/A-2 and -3 forbid and PDF/A-1 counts as a glyph the program lacks. The program gets an empty glyph for it
//   (one per width, named "space" in a version 2 post table) with the advance the dictionary gives the code, and the
//   code maps to it: a simple font through its (3,0) cmap (it becomes symbolic as above), a CIDFont through its
//   CIDToGIDMap. Viewers drew nothing or a box there; now nothing. Only when every use of the font was followed (the
//   codes are known), and not for variable programs (gvar).
//
// - The same for Type 1 programs (FontFile: an empty charstring under the glyph name the code's encoding gives, in the
//   encrypted part, written as the font's own charstrings are), name-keyed CFF ones (FontFile3 /Type1C) and CID-keyed
//   CFF ones of CIDFontType0 fonts with an Identity encoding (FontFile3 /CIDFontType0C: the CID added to the charset).
//   A code whose encoding names no glyph gets a name of its own ("g65") through the Encoding's Differences. The
//   descriptor's CharSet lists the new names; a CIDSet, which would have to list the new CIDs, is left out (PDF/A-2
//   doesn't ask for one). Only when every use of the font was followed; OpenType (FontFile3 /OpenType) programs and
//   CFF programs whose widths or glyphs the dictionary doesn't match otherwise are left as they are.
//
// A program that can't be read is left as it is (the PDF/A then shows the problem as before).

using System.Buffers.Binary;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
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
                    case "/Type1" or "/MMType1" when codes is { Count: > 0 }:
                        RepairType1(document, font, codes);
                        break;
                    case "/Type0" when codes is { Count: > 0 } && Name(font, "/Encoding") is "/Identity-H" or "/Identity-V" && Descendants(font) is [var cidFont, ..]
                                       && Name(cidFont, "/Subtype") == "/CIDFontType0":
                        RepairCidCff(document, cidFont, codes);
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
        var lacking = new List<int>();
        foreach (var code in used?.Where(c => c is >= 0 and < 256) ?? Enumerable.Range(firstListed, lastListed - firstListed + 1))
        {
            if (Drawn(code) is { } glyph && glyph < sfnt.GlyphCount)
                glyphs[code] = glyph;
            else if (used is not null)
                lacking.Add(code);
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

        // Widths: the dictionary's, else its MissingWidth.
        var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
        var array = PdfObjects.Array(font, "/Widths");
        var missing = PdfObjects.Number(PdfObjects.Get(descriptor, "/MissingWidth")) ?? 0;
        double Width(int code) => array is not null && code - first >= 0 && code - first < array.Elements.Count
            && PdfObjects.Number(array.Elements[code - first]) is { } w ? w : missing;

        // Codes drawn that have no glyph get empty ones, through a (3,0) cmap like a forbidden encoding.
        var added = lacking.Count > 0 ? AddEmptyGlyphs(sfnt, lacking.ToDictionary(c => c, Width)) : [];
        if (added.Count > 0)
        {
            foreach (var (code, glyph) in added)
                glyphs[code] = glyph;
            encodingKept = false;
            programChanged = true;
        }
        if (!encodingKept)
        {
            // Every code that draws something keeps its glyph through a (3,0) cmap of its own.
            var symbolMap = new Dictionary<int, int>();
            for (var code = 0; code < 256; code++)
            {
                if (added.TryGetValue(code, out var empty))
                    symbolMap[0xF000 + code] = empty;
                else if (Drawn(code) is { } glyph && glyph < sfnt.GlyphCount)
                    symbolMap[0xF000 + code] = glyph;
            }
            sfnt.Tables["cmap"] = CmapTable.Build(symbolMap, symbol: true);
            font.Elements.Remove("/Encoding");
            flags = (flags | Symbolic) & ~Nonsymbolic;
            programChanged = true;
        }

        // Widths: the program follows the dictionary; a glyph drawn with two widths keeps the first.
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
        var lacking = new List<int>();
        foreach (var cid in used?.Where(c => c is >= 0 and < MaxCids) ?? widths.Keys)
        {
            if (Glyph(cid) is var glyph and > 0 && glyph < sfnt.GlyphCount)
                glyphs[cid] = glyph;
            else if (used is not null)
                lacking.Add(cid);
        }
        // CIDs drawn that have no glyph get empty ones, through a CIDToGIDMap of their own.
        var added = lacking.Count > 0 ? AddEmptyGlyphs(sfnt, lacking.ToDictionary(c => c, c => widths.TryGetValue(c, out var w) ? w : standard)) : [];
        if (added.Count > 0)
        {
            var size = Math.Max(map is null ? 0 : map.Length / 2, Math.Max(used!.Max(), added.Keys.Max()) + 1);
            var rebuilt = new byte[2 * size];
            for (var cid = 0; cid < size; cid++)
                BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(2 * cid), (ushort)(added.TryGetValue(cid, out var empty) ? empty : Math.Min(Glyph(cid), 0xFFFF)));
            var mapStream = new PdfDictionary(document);
            mapStream.CreateStream(rebuilt);
            mapStream.Stream.Zip();
            document.Internals.AddObject(mapStream);
            cidFont.Elements["/CIDToGIDMap"] = mapStream.Reference;
            foreach (var (cid, glyph) in added)
                glyphs[cid] = glyph;
        }
        var wanted = glyphs.ToDictionary(p => p.Key, p => widths.TryGetValue(p.Key, out var w) ? w : standard);
        var (advancesChanged, keep) = FitAdvances(sfnt, glyphs, wanted);
        advancesChanged |= added.Count > 0;
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

    // ---- Type 1 and CFF programs

    /// <summary>
    /// A simple Type 1 font (FontFile, or a bare CFF FontFile3 /Type1C) whose drawn codes name glyphs its program
    /// lacks: an empty glyph under that name, with the code's width, is added to the program. A code that names no
    /// glyph (or .notdef) gets a name of its own in the Encoding's Differences first.
    /// </summary>
    private static void RepairType1(PdfDocument document, PdfDictionary font, HashSet<int> used)
    {
        if (Resolve(font.Elements["/FontDescriptor"]) is not PdfDictionary descriptor)
            return;
        var symbolic = (Integer(descriptor, "/Flags") & Symbolic) != 0;
        var type1File = Resolve(descriptor.Elements["/FontFile"]) as PdfDictionary;
        var cffFile = Resolve(descriptor.Elements["/FontFile3"]) as PdfDictionary;
        byte[] bytes;
        string?[] builtIn;
        Func<string, bool> has;
        double scale; // 1/1000 em → glyph space
        if (type1File?.Stream is not null)
        {
            bytes = type1File.Stream.UnfilteredValue;
            var program = Type1Program.Read(bytes);
            (builtIn, has, scale) = (program.Encoding, program.HasGlyph, 0.001 / program.FontMatrix[0]);
        }
        else if (cffFile?.Stream is not null && Name(cffFile, "/Subtype") == "/Type1C")
        {
            bytes = cffFile.Stream.UnfilteredValue;
            var program = CffProgram.Read(bytes);
            if (program.IsCid)
                return;
            (builtIn, has, scale) = (program.BuiltInEncoding(), name => program.GlyphOfName(name) >= 0, 0.001 / CffFont.Parse(bytes).FontMatrices[0][0]);
        }
        else
        {
            return;
        }
        if (bytes.Length == 0 || bytes.Length > PdfFontUses.MaxStreamSize * 8L || !double.IsFinite(scale) || scale <= 0)
            return;

        var names = SimpleEncodings.Resolve(font, builtIn, out _, symbolic);
        var first = (int)PdfObjects.Number(font, "/FirstChar", 0);
        var array = PdfObjects.Array(font, "/Widths");
        var missing = PdfObjects.Number(PdfObjects.Get(descriptor, "/MissingWidth")) ?? 0;
        double Width(int code) => array is not null && code - first >= 0 && code - first < array.Elements.Count
            && PdfObjects.Number(array.Elements[code - first]) is { } w ? w : missing;

        var glyphs = new List<(string Name, double Width)>();
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        var renamed = new Dictionary<int, string>();
        foreach (var code in used.Where(c => c is >= 0 and < 256).Order())
        {
            var name = names[code];
            if (name is not null && name != ".notdef" && has(name))
                continue;
            if (name is null or ".notdef" || name.Any(c => c <= ' ' || c > '~' || "/[]{}()<>%".Contains(c)))
            {
                // No glyph name to give: one of its own.
                var own = $"g{code}";
                for (var n = 2; has(own) || names.Contains(own); n++)
                    own = $"g{code}.{n}";
                renamed[code] = name = own;
            }
            if (widths.TryAdd(name, Width(code)))
                glyphs.Add((name, Width(code) * scale));
        }
        if (glyphs.Count == 0)
            return;

        var copy = DescriptorCopy(document, descriptor);
        if (type1File is not null)
        {
            var rewritten = Type1Program.AddEmptyGlyphs(bytes, glyphs, (int)PdfObjects.Number(type1File, "/Length1", 0), (int)PdfObjects.Number(type1File, "/Length2", 0));
            var file = new PdfDictionary(document);
            file.CreateStream(rewritten.Data);
            file.Elements.SetInteger("/Length1", rewritten.Length1);
            file.Elements.SetInteger("/Length2", rewritten.Length2);
            file.Elements.SetInteger("/Length3", rewritten.Length3);
            file.Stream.Zip();
            document.Internals.AddObject(file);
            copy.Elements["/FontFile"] = file.Reference;
        }
        else
        {
            var cff = CffSubsetter.AddGlyphs(bytes, [.. glyphs.Select(g => new CffSubsetter.NewGlyph(g.Name, 0, g.Width))]);
            copy.Elements["/FontFile3"] = CffStream(document, cff, "/Type1C");
        }
        // CharSet lists every glyph of the program when it is there.
        if (Resolve(copy.Elements["/CharSet"]) is PdfString charSet)
            copy.Elements["/CharSet"] = new PdfString(charSet.Value + string.Concat(glyphs.Select(g => "/" + g.Name)));
        font.Elements["/FontDescriptor"] = copy.Reference;

        // Codes with a name of their own: the Encoding names them (every name it gave, so nothing else changes).
        if (renamed.Count > 0)
        {
            var encoding = new PdfDictionary(document);
            encoding.Elements.SetName("/Type", "/Encoding");
            if (Resolve(font.Elements["/Encoding"]) is PdfName { Value: "/WinAnsiEncoding" or "/MacRomanEncoding" or "/MacExpertEncoding" } baseName)
                encoding.Elements["/BaseEncoding"] = baseName;
            else if (Resolve(font.Elements["/Encoding"]) is PdfDictionary old && Resolve(old.Elements["/BaseEncoding"]) is PdfName oldBase)
                encoding.Elements["/BaseEncoding"] = oldBase;
            var differences = new PdfArray(document);
            var next = -2;
            for (var code = 0; code < 256; code++)
            {
                var name = renamed.TryGetValue(code, out var own) ? own : names[code];
                if (name is null)
                    continue;
                if (code != next)
                    differences.Elements.Add(new PdfInteger(code));
                differences.Elements.Add(new PdfName("/" + name));
                next = code + 1;
            }
            encoding.Elements["/Differences"] = differences;
            font.Elements["/Encoding"] = encoding;
        }
    }

    /// <summary>
    /// A CIDFontType0 with an Identity encoding (codes are CIDs) whose CID-keyed CFF program (FontFile3
    /// /CIDFontType0C) lacks CIDs it draws: an empty glyph with the CID's width is added for each.
    /// </summary>
    private static void RepairCidCff(PdfDocument document, PdfDictionary cidFont, HashSet<int> used)
    {
        if (Resolve(cidFont.Elements["/FontDescriptor"]) is not PdfDictionary descriptor
            || Resolve(descriptor.Elements["/FontFile3"]) is not PdfDictionary { Stream: not null } file || Name(file, "/Subtype") != "/CIDFontType0C")
            return;
        var bytes = file.Stream.UnfilteredValue;
        if (bytes.Length == 0 || bytes.Length > PdfFontUses.MaxStreamSize * 8L)
            return;
        var parsed = CffFont.Parse(bytes);
        if (!parsed.IsCid || parsed.Charset is null)
            return;
        var present = new HashSet<int>(parsed.Charset.Select(c => (int)c));
        var widths = CidWidths(cidFont);
        var standard = PdfObjects.Number(cidFont, "/DW", 1000);
        var scale = 0.001 / parsed.FontMatrices[0][0];
        if (!double.IsFinite(scale) || scale <= 0)
            return;
        // CID 0 is .notdef itself: nothing to add for it.
        var lacking = used.Where(cid => cid is > 0 and < MaxCids && !present.Contains(cid)).Order()
            .Select(cid => new CffSubsetter.NewGlyph(null, cid, (widths.TryGetValue(cid, out var w) ? w : standard) * scale)).ToList();
        if (lacking.Count == 0)
            return;
        var cff = CffSubsetter.AddGlyphs(bytes, lacking);
        var copy = DescriptorCopy(document, descriptor);
        copy.Elements["/FontFile3"] = CffStream(document, cff, "/CIDFontType0C");
        copy.Elements.Remove("/CIDSet"); // it would have to list the new CIDs; PDF/A-2 lets it be left out
        cidFont.Elements["/FontDescriptor"] = copy.Reference;
    }

    /// <summary>A FontFile3 stream of a CFF program.</summary>
    private static PdfReference CffStream(PdfDocument document, byte[] cff, string subtype)
    {
        var file = new PdfDictionary(document);
        file.CreateStream(cff);
        file.Elements.SetName("/Subtype", subtype);
        file.Stream.Zip();
        document.Internals.AddObject(file);
        return file.Reference!;
    }

    /// <summary>A copy of a font descriptor (the old one may be shared), added to the document.</summary>
    private static PdfDictionary DescriptorCopy(PdfDocument document, PdfDictionary descriptor)
    {
        var copy = new PdfDictionary(document);
        foreach (var key in descriptor.Elements.Keys)
            copy.Elements[key] = descriptor.Elements[key];
        document.Internals.AddObject(copy);
        return copy;
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

    /// <summary>
    /// Adds an empty glyph for each width among <paramref name="widths"/> (code → width, 1/1000 em) at the end of a
    /// TrueType program: loca, maxp, hmtx (one metric a glyph), hhea, post (named "space") and vmtx follow; hdmx and
    /// LTSH, which list every glyph, are dropped. Returns code → its new glyph; empty when the program can't take them
    /// (CFF or variable outlines, 65,535 glyphs).
    /// </summary>
    internal static Dictionary<int, int> AddEmptyGlyphs(SfntFont sfnt, Dictionary<int, double> widths)
    {
        var result = new Dictionary<int, int>();
        if (widths.Count == 0 || sfnt["glyf"] is null || sfnt["loca"] is not { } loca || sfnt["gvar"] is not null || sfnt.HasCffOutlines)
            return result;
        var head = sfnt.Require("head");
        var hhea = sfnt.Require("hhea");
        var hmtx = sfnt.Require("hmtx");
        var count = sfnt.GlyphCount;
        var unitsPerEm = FontTables.UnitsPerEm(head);
        var metrics = FontTables.NumberOfMetrics(hhea);
        var longOffsets = head.Length >= 52 && BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) == 1;
        var entry = longOffsets ? 4 : 2;
        if (unitsPerEm == 0 || metrics == 0 || hmtx.Length < metrics * 4 || hhea.Length < 36 || loca.Length < (count + 1) * entry)
            throw new InvalidDataException("The font's tables are damaged.");
        // One glyph a width, in font units.
        var byUnits = new Dictionary<int, int>();
        var advances = new List<int>();
        foreach (var (code, width) in widths.OrderBy(p => p.Key))
        {
            var units = (int)Math.Clamp(Math.Round(width * unitsPerEm / 1000), 0, ushort.MaxValue);
            if (!byUnits.TryGetValue(units, out var glyph))
            {
                if (count + advances.Count >= ushort.MaxValue)
                    return [];
                byUnits[units] = glyph = count + advances.Count;
                advances.Add(units);
            }
            result[code] = glyph;
        }
        var total = count + advances.Count;

        // loca: the new glyphs have no data (each starts where the font's data ends).
        var newLoca = new byte[(total + 1) * entry];
        loca.AsSpan(0, (count + 1) * entry).CopyTo(newLoca);
        for (var glyph = count + 1; glyph <= total; glyph++)
            loca.AsSpan(count * entry, entry).CopyTo(newLoca.AsSpan(glyph * entry));
        sfnt.Tables["loca"] = newLoca;
        var maxp = (byte[])sfnt.Require("maxp").Clone();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)total);
        sfnt.Tables["maxp"] = maxp;

        // hmtx: one advance and bearing a glyph.
        var table = new byte[total * 4];
        var widest = 0;
        for (var glyph = 0; glyph < total; glyph++)
        {
            int advance;
            short bearing = 0;
            if (glyph < count)
            {
                advance = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(Math.Min(glyph, metrics - 1) * 4));
                var at = glyph < metrics ? glyph * 4 + 2 : metrics * 4 + (glyph - metrics) * 2;
                bearing = at + 2 <= hmtx.Length ? BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(at)) : (short)0;
            }
            else
            {
                advance = advances[glyph - count];
            }
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(glyph * 4), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(glyph * 4 + 2), bearing);
            widest = Math.Max(widest, advance);
        }
        sfnt.Tables["hmtx"] = table;
        var header = (byte[])hhea.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)widest);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34), (ushort)total);
        sfnt.Tables["hhea"] = header;

        // post: version 2 names the new glyphs "space" (standard name 3); version 1 (the 258 Macintosh glyphs) and
        // 2.5 no longer fit and become version 3 (no names).
        if (sfnt["post"] is { Length: >= 32 } post)
        {
            var version = BinaryPrimitives.ReadUInt32BigEndian(post);
            if (version == 0x00020000 && post.Length >= 34 && BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(32)) == count && post.Length >= 34 + 2 * count)
            {
                var names = new byte[post.Length + 2 * advances.Count];
                post.AsSpan(0, 34 + 2 * count).CopyTo(names);
                BinaryPrimitives.WriteUInt16BigEndian(names.AsSpan(32), (ushort)total);
                for (var i = 0; i < advances.Count; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(names.AsSpan(34 + 2 * (count + i)), 3);
                post.AsSpan(34 + 2 * count).CopyTo(names.AsSpan(34 + 2 * total));
                sfnt.Tables["post"] = names;
            }
            else if (version != 0x00030000)
            {
                var plain = post.AsSpan(0, 32).ToArray();
                BinaryPrimitives.WriteUInt32BigEndian(plain, 0x00030000);
                sfnt.Tables["post"] = plain;
            }
        }
        sfnt.Tables.Remove("hdmx");
        sfnt.Tables.Remove("LTSH");
        if (sfnt["vmtx"] is { } vmtx)
            sfnt.Tables["vmtx"] = [.. vmtx, .. new byte[2 * advances.Count]]; // top side bearings 0
        return result;
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
        copy.Elements.Remove("/CIDSet"); // the CIDs present may have changed; PDF/A-2 lets it be left out
        document.Internals.AddObject(copy);
        font.Elements["/FontDescriptor"] = copy.Reference;
    }
}
