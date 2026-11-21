// PDF/A-2b of PDFs whose own TrueType fonts break the font rules (Pdf/PdfAFontRepair.cs): widths that differ from the
// program, a symbolic font with an encoding, a non-symbolic one with StandardEncoding, no ToUnicode, a CIDFontType2
// with other widths; composite fonts with a predefined Unicode CMap (UniKS-UCS2-H, UniJIS-UCS2-H) embedded from the
// computer's CJK fonts; and the width check of PdfAFonts against Windows' and Liberation's replacements of the standard
// fonts; Type 1 and CFF programs that lack glyphs the PDF draws; the widths and vertical metrics of Unicode CMap fonts
// by the CMap file. The PDFs are written in code (RawPdf, PdfATestFonts' program, PostScriptPdfBuilder's Type 1
// program and the test CFF fonts); tests of installed fonts skip without them.

using Filee.Core.Conversion;
using Filee.Engines.Pdf;
using Filee.Engines.Typesetting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfAFontRepairTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private PdfArchiveOutcome Archive(string path, IFontCollection fonts) => PdfSharpConverter.Archive(path, changed: true, fx.NewFolder(), Ct, fonts);

    private static PdfDictionary Font(PdfDocument document, string name = "F1") =>
        (PdfDictionary)V(document.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements["/" + name])!;

    private static string Text(string path)
    {
        using var pig = PigDocument.Open(path);
        return string.Concat(pig.GetPage(1).Text.Where(c => !char.IsWhiteSpace(c)));
    }

    /// <summary>A page drawing <paramref name="text"/> with the test font as /F1 (object 5), its descriptor 6 and program 7.</summary>
    private static string TrueTypePage(string path, string text, string font, int flags = 32) =>
        PdfAEmbeddingTests.Page(path, $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET", "/Font << /F1 5 0 R >>",
            extra: [font, PdfATestFonts.Descriptor("FileeTest-TrueType", 7, flags), PdfATestFonts.Program()]);

    [Fact]
    public void Widths_that_differ_from_the_program_are_given_to_the_program_so_the_text_keeps_its_places()
    {
        var path = TrueTypePage(Path.Combine(fx.NewFolder(), "widths.pdf"), "Hamburgefonstiv", PdfATestFonts.TrueTypeFont(6, extraWidth: 50));
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.2.11.5", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            // The dictionary's widths are the ones the PDF had: the text stays where it was.
            var widths = (PdfArray)V(Font(document).Elements["/Widths"])!;
            Assert.Equal(PdfATestFonts.Advance('H') + 50, ((PdfNumber)V(widths.Elements['H' - 32])!) switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => 0 }, 1);
            Assert.True(Font(document).Elements.ContainsKey("/ToUnicode"));
        }
        Assert.Equal("Hamburgefonstiv", Text(path));
    }

    [Theory]
    [InlineData(4, "/Encoding /WinAnsiEncoding")] // symbolic, yet with an encoding and only a Unicode cmap
    [InlineData(32, "/Encoding /StandardEncoding")] // non-symbolic with an encoding PDF/A doesn't allow
    [InlineData(32, "")] // non-symbolic without an encoding
    public void Encodings_pdf_a_forbids_become_a_symbolic_font_with_its_own_cmap(int flags, string encoding)
    {
        var path = TrueTypePage(Path.Combine(fx.NewFolder(), "encoding.pdf"), "Hamburgefonstiv", PdfATestFonts.TrueTypeFont(6, encoding: encoding), flags);
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.2.11.6", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var font = Font(document);
            Assert.False(font.Elements.ContainsKey("/Encoding"));
            Assert.Equal(4, ((PdfDictionary)V(font.Elements["/FontDescriptor"])!).Elements.GetInteger("/Flags") & 4);
        }
        Assert.Equal("Hamburgefonstiv", Text(path));
    }

    [Fact]
    public void A_cid_truetype_font_gets_its_widths_into_the_program_and_its_text_from_the_cmap()
    {
        // CIDs as glyph IDs (Identity), widths 30 wider than the program's, no ToUnicode.
        using var sfnt = PageFontOf();
        var (h, i) = (Glyph('H'), Glyph('i'));
        var path = PdfAEmbeddingTests.Page(Path.Combine(fx.NewFolder(), "cid.pdf"), $"BT /F1 24 Tf 72 700 Td <{h:X4}{i:X4}> Tj ET", "/Font << /F1 5 0 R >>", extra:
        [
            "<< /Type /Font /Subtype /Type0 /BaseFont /FileeTest-TrueType /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FileeTest-TrueType /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/CIDToGIDMap /Identity /DW 1000 /W [{h} [{PdfATestFonts.Advance('H') + 30}] {i} [{PdfATestFonts.Advance('i') + 30}]] /FontDescriptor 7 0 R >>",
            PdfATestFonts.Descriptor("FileeTest-TrueType", 8, 4),
            PdfATestFonts.Program(),
        ]);
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.2.11.5", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        Assert.Equal("Hi", Text(path));

        static Filee.Engines.Rendering.PageFont PageFontOf() =>
            Filee.Engines.Rendering.PageFont.FromData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")));
        int Glyph(char c) => sfnt.Typeface.GetGlyph(c);
    }

    [Fact]
    public void A_code_the_program_has_no_glyph_for_gets_an_empty_glyph_instead_of_notdef()
    {
        // The test font is a subset without Q or X: those codes draw .notdef.
        Assert.Equal(0, PdfATestFonts.Advance('Q'));
        var path = TrueTypePage(Path.Combine(fx.NewFolder(), "lacking.pdf"), "HiQX", PdfATestFonts.TrueTypeFont(6));
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.2.11.8", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        Assert.Equal("HiQX", Text(path));
    }

    [Fact]
    public void A_cid_the_program_has_no_glyph_for_gets_an_empty_glyph_through_the_cid_to_gid_map()
    {
        using var sfnt = Filee.Engines.Rendering.PageFont.FromData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")));
        var h = sfnt.Typeface.GetGlyph('H');
        var beyond = sfnt.Typeface.GlyphCount + 5;
        var path = PdfAEmbeddingTests.Page(Path.Combine(fx.NewFolder(), "cid-lacking.pdf"), $"BT /F1 24 Tf 72 700 Td <{h:X4}{beyond:X4}0000> Tj ET",
            "/Font << /F1 5 0 R >>", extra:
            [
                "<< /Type /Font /Subtype /Type0 /BaseFont /FileeTest-TrueType /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
                "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FileeTest-TrueType /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                    $"/CIDToGIDMap /Identity /DW 600 /W [{h} [{PdfATestFonts.Advance('H')}]] /FontDescriptor 7 0 R >>",
                PdfATestFonts.Descriptor("FileeTest-TrueType", 8, 4),
                PdfATestFonts.Program(),
            ]);
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.2.11.8", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
    }

    [Fact]
    public void Glyphs_added_to_a_program_keep_its_tables_in_step()
    {
        var sfnt = Filee.Engines.Fonts.SfntFont.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")));
        var count = sfnt.GlyphCount;

        var added = PdfAFonts.AddEmptyGlyphs(sfnt, new Dictionary<int, double> { [1] = 600, [2] = 600, [3] = 250 });

        // One glyph a width, at the end; every table that counts glyphs counts them.
        Assert.Equal(new Dictionary<int, int> { [1] = count, [2] = count, [3] = count + 1 }, added);
        Assert.Equal(count + 2, sfnt.GlyphCount);
        Assert.Equal(count + 2, Filee.Engines.Fonts.FontTables.NumberOfMetrics(sfnt.Require("hhea")));
        Assert.Equal((count + 2) * 4, sfnt.Require("hmtx").Length);
        var post = sfnt.Require("post");
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(post) == 0x00020000)
            Assert.Equal(count + 2, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(32)));
        using var font = Filee.Engines.Rendering.PageFont.FromData(sfnt.Write());
        Assert.Equal(count + 2, font.Typeface.GlyphCount);
    }

    [Fact]
    public void Widths_of_a_unicode_cmap_are_kept_where_the_cid_is_known_without_the_cmap()
    {
        // W gives CIDs only the default width: every code has it.
        Assert.Equal(1000, PdfAFonts.UnicodeWidth('가', [], 1000, halfWidth: false));
        Assert.Equal(1000, PdfAFonts.UnicodeWidth('A', new Dictionary<int, double> { [5000] = 1000 }, 1000, halfWidth: false));
        // Printable ASCII: CID code − 31 (A is CID 34), else DW; backslash, tilde and half-width CMaps unknown.
        Dictionary<int, double> proportional = new() { [1] = 250, [34] = 640 };
        Assert.Equal(640, PdfAFonts.UnicodeWidth('A', proportional, 1000, halfWidth: false));
        Assert.Equal(250, PdfAFonts.UnicodeWidth(' ', proportional, 1000, halfWidth: false));
        Assert.Equal(1000, PdfAFonts.UnicodeWidth('B', proportional, 1000, halfWidth: false));
        Assert.Null(PdfAFonts.UnicodeWidth('\\', proportional, 1000, halfWidth: false));
        Assert.Null(PdfAFonts.UnicodeWidth('A', proportional, 1000, halfWidth: true));
        Assert.Null(PdfAFonts.UnicodeWidth('가', proportional, 1000, halfWidth: false));
    }

    // ---- predefined Unicode CMaps

    /// <summary>A page drawing UTF-16 <paramref name="text"/> with a CID font that isn't embedded, through a predefined CMap.</summary>
    private static string PredefinedPage(string path, string text, string cmap, string baseFont, string ordering, string widths = "[1 [500]]") =>
        PdfAEmbeddingTests.Page(path, $"BT /F1 24 Tf 72 700 Td <{Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(text))}> Tj ET",
            "/Font << /F1 5 0 R >>", extra:
            [
                $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont}-{cmap} /Encoding /{cmap} /DescendantFonts [6 0 R] >>",
                $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 2 >> " +
                    $"/DW 1000 /W {widths} /FontDescriptor 7 0 R >>",
                $"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 4 /FontBBox [0 -120 1000 880] /ItalicAngle 0 /Ascent 880 /Descent -120 " +
                    "/CapHeight 700 /StemV 80 >>",
            ]);

    [Theory]
    [InlineData("UniKS-UCS2-H", "HYGoThic-Medium", "Korea1", "가나다라", new[] { "Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Gulim", "Batang" })]
    [InlineData("UniJIS-UCS2-H", "KozGoPr6N-Medium", "Japan1", "日本語テキスト", new[] { "Yu Gothic", "MS Gothic", "Meiryo", "Noto Sans CJK JP", "Noto Sans JP", "IPAGothic" })]
    public void A_cid_font_with_a_predefined_unicode_cmap_is_embedded_from_an_installed_cjk_font(string cmap, string baseFont, string ordering, string text,
        string[] families)
    {
        Assert.SkipUnless(families.Any(f => SystemFonts.Instance.Family(f).Count > 0), $"None of {string.Join(", ", families)} is installed");
        var path = PredefinedPage(Path.Combine(fx.NewFolder(), "cjk.pdf"), text, cmap, baseFont, ordering);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, SystemFonts.Instance));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var font = Font(document);
            Assert.Equal("/Identity-H", font.Elements.GetName("/Encoding"));
            var cidFont = (PdfDictionary)V(((PdfArray)V(font.Elements["/DescendantFonts"])!).Elements[0])!;
            Assert.Equal("/CIDFontType2", cidFont.Elements.GetName("/Subtype"));
        }
        Assert.Equal(text, Text(path));
    }

    [Fact]
    public void A_unicode_cmap_font_keeps_the_ascii_widths_the_pdf_gives()
    {
        string[] families = ["Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Gulim", "Batang"];
        Assert.SkipUnless(families.Any(f => SystemFonts.Instance.Family(f).Count > 0), $"None of {string.Join(", ", families)} is installed");
        var path = PredefinedPage(Path.Combine(fx.NewFolder(), "cjk-widths.pdf"), "가A", "UniKS-UCS2-H", "HYGoThic-Medium", "Korea1", "[34 [640]]");

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, SystemFonts.Instance));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var cidFont = (PdfDictionary)V(((PdfArray)V(Font(document).Elements["/DescendantFonts"])!).Elements[0])!;
        var widths = ((PdfArray)V(cidFont.Elements["/W"])!).Elements.Select(V).ToList();
        var at = widths.FindIndex(e => e is PdfInteger { Value: 'A' });
        Assert.True(at >= 0);
        var width = ((PdfArray)widths[at + 1]!).Elements[0] switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => 0 };
        Assert.InRange(width, 639, 641); // 640 at the program's units
    }

    [Fact]
    public void Predefined_cmaps_give_each_code_its_cid_from_adobes_files()
    {
        // Printable ASCII is CIDs 1–95 in the Korean and Japanese collections; a -V CMap builds on its -H one.
        var korean = PdfPredefinedCMaps.Cids("UniKS-UCS2-H");
        Assert.NotNull(korean);
        Assert.Equal(34, korean['A']);
        Assert.True(korean.TryGetValue('가', out var hangul) && hangul > 95, $"{hangul}");
        var vertical = PdfPredefinedCMaps.Cids("/UniKS-UCS2-V");
        Assert.NotNull(vertical);
        Assert.Equal(34, vertical['A']);
        Assert.Equal(hangul, vertical['가']);
        Assert.Contains(vertical, p => korean.TryGetValue(p.Key, out var horizontal) && horizontal != p.Value); // its vertical forms
        Assert.Equal(34, PdfPredefinedCMaps.Cids("UniJIS-UCS2-H")!['A']);
        Assert.Null(PdfPredefinedCMaps.Cids("No-Such-CMap"));
        Assert.Null(PdfPredefinedCMaps.Cids("../UniKS-UCS2-H"));
    }

    [Fact]
    public void A_cmap_that_maps_too_much_or_names_one_filee_cant_read_is_refused_quickly()
    {
        var huge = new System.Text.StringBuilder("begincmap\n");
        for (var i = 0; i < 40; i++)
            huge.Append("1 begincidrange <0000> <ffff> 1 endcidrange\n");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(PdfPredefinedCMaps.Parse(System.Text.Encoding.ASCII.GetBytes(huge.ToString())));
        // A CMap that uses one Filee can't read is refused rather than read in part.
        Assert.Null(PdfPredefinedCMaps.Parse("/No-Such-CMap usecmap 1 begincidchar <0041> 34 endcidchar"u8.ToArray()));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), stopwatch.Elapsed.ToString());
    }

    /// <summary>
    /// A vertical Unicode CMap font: every code keeps the PDF's width and vertical metrics (W2), by the CID the CMap
    /// file gives it, now under the code (the new CID).
    /// </summary>
    [Fact]
    public void A_unicode_cmap_font_keeps_every_width_and_vertical_metric_by_its_cmap()
    {
        string[] families = ["Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Gulim", "Batang"];
        Assert.SkipUnless(families.Any(f => SystemFonts.Instance.Family(f).Count > 0), $"None of {string.Join(", ", families)} is installed");
        var hangul = PdfPredefinedCMaps.Cids("UniKS-UCS2-V")!['가'];
        var path = PredefinedPage(Path.Combine(fx.NewFolder(), "cjk-vertical.pdf"), "가A", "UniKS-UCS2-V", "HYGoThic-Medium", "Korea1",
            $"[34 [640] {hangul} [900]] /W2 [{hangul} [-950 450 870]]");

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, SystemFonts.Instance));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal("/Identity-V", Font(document).Elements.GetName("/Encoding"));
        var cidFont = (PdfDictionary)V(((PdfArray)V(Font(document).Elements["/DescendantFonts"])!).Elements[0])!;
        double WidthOf(int code)
        {
            var widths = ((PdfArray)V(cidFont.Elements["/W"])!).Elements.Select(V).ToList();
            var at = widths.FindIndex(e => e is PdfInteger i && i.Value == code);
            return at >= 0 ? Number(((PdfArray)widths[at + 1]!).Elements[0]) : double.NaN;
        }
        Assert.InRange(WidthOf('가'), 899, 901); // the PDF's, not the installed font's
        Assert.InRange(WidthOf('A'), 639, 641);
        var vertical = ((PdfArray)V(cidFont.Elements["/W2"])!).Elements.Select(V).ToList();
        Assert.Equal('가', ((PdfInteger)vertical[0]!).Value);
        Assert.Equal([-950.0, 450, 870], ((PdfArray)vertical[1]!).Elements.Select(Number));
    }

    private static double Number(PdfItem? item) => V(item) switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => double.NaN };

    // ---- Type 1 and CFF programs lacking glyphs

    private static byte[] FontBytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name));

    /// <summary>
    /// A page drawing with a Type 1 program lacking "D" (code 68 by WinAnsiEncoding) and code 1 (no name at all), a
    /// bare CFF program (Type1C) whose encoding names a glyph "zzz" it lacks, and a CID-keyed CFF program drawing a CID
    /// it lacks.
    /// </summary>
    private static string LackingPage(string folder, out int lackingCid)
    {
        static PdfName N(string name) => PostScriptPdfBuilder.Name(name);
        static PdfItem I(double value) => PostScriptPdfBuilder.Number(value);
        using var b = new PostScriptPdfBuilder();
        var type1 = b.Type1File(PostScriptPdfBuilder.Type1Program("FileeTestType1"));
        var type1Font = b.SimpleFont("Type1", "FileeTestType1", b.Dictionary(("BaseEncoding", N("WinAnsiEncoding")), ("Differences", b.Array(I(65), N("A")))),
            b.Descriptor("FileeTestType1", 32, "FontFile", type1), _ => 620);
        type1Font.Elements.SetInteger("/FirstChar", 1);
        type1Font.Elements["/Widths"] = b.Numbers([.. Enumerable.Range(1, 255).Select(c => c is 1 ? 444.0 : c == 68 ? 555 : 620)]);
        ((PdfDictionary)V(type1Font.Elements["/FontDescriptor"])!).Elements["/CharSet"] = new PdfString("/.notdef/space/square/bowl/flexed/A/acute/Aacute");

        var nameKeyed = Filee.Engines.Fonts.SfntFont.Read(FontBytes("FileeTest-NameKeyed.otf")).Require("CFF ");
        var cffFont = b.SimpleFont("Type1", "FileeTestNameKeyed", b.Dictionary(("Type", N("Encoding")), ("Differences", b.Array(I(65), N("A"), N("zzz")))),
            b.Descriptor("FileeTestNameKeyed", 4, "FontFile3", b.Stream(nameKeyed, false, ("Subtype", N("Type1C")))), c => c == 66 ? 333 : 640);

        var cid = Filee.Engines.Fonts.SfntFont.Read(FontBytes("FileeTest-CID.otf")).Require("CFF ");
        var charset = Filee.Engines.Fonts.Cff.CffFont.Parse(cid).Charset!;
        lackingCid = charset.Max() + 7;
        var present = charset[1];
        var descriptor = b.Descriptor("FileeTest", 4, "FontFile3", b.Stream(cid, false, ("Subtype", N("CIDFontType0C"))));
        descriptor.Elements["/CIDSet"] = b.Stream([0xFF]).Reference!;
        var cidFont = b.Object(("Type", N("Font")), ("Subtype", N("CIDFontType0")), ("BaseFont", N("FileeTest")),
            ("CIDSystemInfo", b.Dictionary(("Registry", new PdfString("Adobe")), ("Ordering", new PdfString("Identity")), ("Supplement", I(0)))),
            ("FontDescriptor", descriptor.Reference!), ("DW", I(1000)), ("W", b.Array(I(lackingCid), b.Numbers(777))));
        var composite = b.Object(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("FileeTest")),
            ("Encoding", N("Identity-H")), ("DescendantFonts", b.Array(cidFont.Reference!)));

        var content = $"BT /F1 40 Tf 40 700 Td (A\\001D) Tj ET\nBT /F2 40 Tf 40 600 Td (AB) Tj ET\nBT /F3 40 Tf 40 500 Td <{present:X4}{lackingCid:X4}> Tj ET\n";
        b.Page(612, 792, content, b.Resources(("Font", "F1", type1Font), ("Font", "F2", cffFont), ("Font", "F3", composite)));
        return b.Save(folder, "lacking.pdf");
    }

    private static byte[] Program(PdfDictionary font, string key)
    {
        var descriptor = (PdfDictionary)V(font.Elements["/FontDescriptor"])!;
        return ((PdfDictionary)V(descriptor.Elements[key])!).Stream.UnfilteredValue;
    }

    [Fact]
    public void Type_1_and_cff_programs_get_the_glyphs_they_lack_as_empty_ones()
    {
        var path = LackingPage(fx.NewFolder(), out var lackingCid);
        using (var before = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var program = Filee.Engines.Pdf.PostScript.Type1Program.Read(Program(Font(before), "/FontFile"));
            Assert.False(program.HasGlyph("D"));
        }

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        // Type 1: "D" under its own name, code 1 under a new one the encoding gives it; both with the PDF's width.
        var type1Font = Font(document);
        var type1 = Filee.Engines.Pdf.PostScript.Type1Program.Read(Program(type1Font, "/FontFile"));
        Assert.True(type1.HasGlyph("A") && type1.HasGlyph("D") && type1.HasGlyph("g1"));
        Assert.Equal(555, type1.Outline("D")!.Value.Width, 0.5);
        Assert.Equal(444, type1.Outline("g1")!.Value.Width, 0.5);
        Assert.Empty(type1.Outline("D")!.Value.Contours);
        Assert.NotEmpty(type1.Outline("A")!.Value.Contours);
        var names = Filee.Engines.Pdf.PostScript.SimpleEncodings.Resolve(type1Font, type1.Encoding, out _, symbolic: false);
        Assert.Equal(("A", "g1", "D"), (names[65], names[1], names[68]));
        var charSet = ((PdfString)V(((PdfDictionary)V(type1Font.Elements["/FontDescriptor"])!).Elements["/CharSet"])!).Value;
        Assert.EndsWith("/Aacute/g1/D", charSet, StringComparison.Ordinal);
        // Name-keyed CFF: "zzz" added (no outline), A kept.
        var cff = Filee.Engines.Pdf.PostScript.CffProgram.Read(Program(Font(document, "F2"), "/FontFile3"));
        Assert.True(cff.GlyphOfName("A") > 0 && cff.GlyphOfName("zzz") > 0);
        Assert.Empty(cff.Outline(cff.GlyphOfName("zzz")));
        Assert.NotEmpty(cff.Outline(cff.GlyphOfName("A")));
        // CID-keyed CFF: the CID added; the CIDSet, which would no longer list every CID, left out.
        var cidFont = (PdfDictionary)V(((PdfArray)V(Font(document, "F3").Elements["/DescendantFonts"])!).Elements[0])!;
        var cid = Filee.Engines.Pdf.PostScript.CffProgram.Read(Program(cidFont, "/FontFile3"));
        Assert.True(cid.GlyphOfCid(lackingCid) > 0);
        Assert.False(((PdfDictionary)V(cidFont.Elements["/FontDescriptor"])!).Elements.ContainsKey("/CIDSet"));
    }

    [Fact]
    public void Empty_glyphs_go_into_a_hex_type_1_program_and_into_a_cff_one_with_their_widths()
    {
        var program = PostScriptPdfBuilder.Type1Program("FileeTestType1");
        var eexec = program.AsSpan().IndexOf("eexec\n"u8) + 6;
        var trailer = program.AsSpan().LastIndexOf("0000000000"u8);
        trailer = program.AsSpan(0, trailer).LastIndexOf((byte)'\n') + 1;
        byte[] hex = [.. program.AsSpan(0, eexec), .. System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(program.AsSpan(eexec, trailer - eexec)) + "\n"),
            .. program.AsSpan(trailer)];

        var rewritten = Filee.Engines.Pdf.PostScript.Type1Program.AddEmptyGlyphs(hex, [("Zeta", 1200), ("space2", 50)]);

        var read = Filee.Engines.Pdf.PostScript.Type1Program.Read(rewritten.Data);
        Assert.True(read.HasGlyph("Zeta") && read.HasGlyph("space2") && read.HasGlyph("bowl"));
        Assert.Equal(1200, read.Outline("Zeta")!.Value.Width, 0.5);
        Assert.Equal(50, read.Outline("space2")!.Value.Width, 0.5);
        Assert.NotEmpty(read.Outline("bowl")!.Value.Contours);
        Assert.Equal(rewritten.Data.Length, rewritten.Length1 + rewritten.Length2 + rewritten.Length3);

        // CFF: glyphs of three widths (one a name the font already has a string for) come after the font's own.
        var cff = Filee.Engines.Fonts.SfntFont.Read(FontBytes("FileeTest-NameKeyed.otf")).Require("CFF ");
        var before = Filee.Engines.Fonts.Cff.CffFont.Parse(cff).GlyphCount;
        var more = Filee.Engines.Fonts.Cff.CffSubsetter.AddGlyphs(cff, [new("one", 0, 250), new("filee.wide", 0, 5000), new("filee.wide2", 0, -40)]);
        var font = Filee.Engines.Pdf.PostScript.CffProgram.Read(more);
        Assert.Equal(before + 3, font.GlyphCount);
        Assert.Equal([before, before + 1, before + 2], new[] { font.GlyphOfName("one"), font.GlyphOfName("filee.wide"), font.GlyphOfName("filee.wide2") });
        Assert.NotEmpty(font.Outline(font.GlyphOfName("A")));
    }

    [Fact]
    public void A_predefined_cmap_whose_codes_are_not_unicode_is_still_refused()
    {
        var path = PredefinedPage(Path.Combine(fx.NewFolder(), "ksc.pdf"), "AB", "KSC-EUC-H", "HYGoThic-Medium", "Korea1");

        var refused = Assert.Throws<ConversionRefusedException>(() => Archive(path, SystemFonts.Instance));

        Assert.Equal(PdfAConformance.FontNotEmbedded, refused.ErrorKey);
    }

    // ---- the width check against installed replacements

    /// <summary>Only the named families of the computer's fonts.</summary>
    private sealed class Only(params string[] families) : IFontCollection
    {
        public IReadOnlyList<FontFace> Family(string name) =>
            families.Contains(name, StringComparer.OrdinalIgnoreCase) ? SystemFonts.Instance.Family(name) : [];

        public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) => null;
    }

    public static TheoryData<string, string> StandardFonts()
    {
        var data = new TheoryData<string, string>();
        foreach (var set in new[] { "windows", "liberation" })
        {
            foreach (var family in new[] { "Helvetica", "Times", "Courier" })
            {
                foreach (var style in new[] { "", "Bold", "Italic", "BoldItalic" })
                {
                    var name = (family, style) switch
                    {
                        ("Times", "") => "Times-Roman",
                        (_, "") => family,
                        ("Times", _) => $"Times-{style}",
                        (_, "Italic") => $"{family}-Oblique",
                        (_, "BoldItalic") => $"{family}-BoldOblique",
                        _ => $"{family}-{style}",
                    };
                    data.Add(name, set);
                }
            }
        }
        return data;
    }

    /// <summary>
    /// Every printable ASCII and some Latin-1 characters in each of the twelve standard fonts, embedded from Windows'
    /// Arial / Times New Roman / Courier New or from Liberation Sans / Serif / Mono alone: the widths the PDF takes from
    /// the standard fonts' metrics must pass the check with either (MaxNear, MaxFar).
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardFonts))]
    public void Every_standard_font_passes_the_width_check_with_windows_and_liberation_fonts(string standard, string set)
    {
        string[] families = set == "windows" ? ["Arial", "Times New Roman", "Courier New"] : ["Liberation Sans", "Liberation Serif", "Liberation Mono"];
        var needed = families[standard.StartsWith("Helvetica", StringComparison.Ordinal) ? 0 : standard.StartsWith("Times", StringComparison.Ordinal) ? 1 : 2];
        Assert.SkipUnless(SystemFonts.Instance.Family(needed).Count > 0, $"{needed} isn't installed");
        var ascii = new string([.. Enumerable.Range(33, 94).Select(c => (char)c)]).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var path = PdfAEmbeddingTests.Page(Path.Combine(fx.NewFolder(), "standard.pdf"),
            $"BT /F1 9 Tf 20 700 Td ({ascii}) Tj 0 -20 Td (ÁáéÅçÑüßøÆ«»©®°±µ¶·¿×÷) Tj ET", "/Font << /F1 5 0 R >>",
            extra: [$"<< /Type /Font /Subtype /Type1 /BaseFont /{standard} /Encoding /WinAnsiEncoding >>"]);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, new Only(families)));

        PdfA2bChecks.Assert2b(path);
    }
}
