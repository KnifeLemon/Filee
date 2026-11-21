// PDF/A-2b from PDFs that lack what PDF/A asks for (Pdf/PdfAFonts.cs, PdfAAppearances.cs, CmykProfile.cs and their
// use in PdfAConformance.cs): fonts that aren't embedded, annotations without appearances, device CMYK without a CMYK
// output intent; the checker (PdfA2bChecks) finding what it should; and PDFs the job queue converts made PDF/A. Every
// input is built in the test; the installed fonts are a collection of the test font (PdfATestFonts), except in the
// test that uses the computer's own Helvetica replacements.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Typesetting;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public partial class PdfAEmbeddingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>One page drawing <paramref name="content"/>; <paramref name="extra"/> objects are 5 ….</summary>
    internal static string Page(string path, string content, string resources = "", string annotations = "", string catalog = "",
        IReadOnlyList<string>? extra = null) =>
        RawPdf.Write(path,
        [
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << {resources} >> {annotations} >>",
            RawPdf.Stream("", content),
            .. extra ?? [],
        ]);

    internal static string SansFont(string name = "FileeSans") =>
        $"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /FirstChar 32 /LastChar 126 /Widths {PdfATestFonts.Widths(32, 126)} /Encoding /WinAnsiEncoding >>";

    private PdfArchiveOutcome Archive(string path, IFontCollection fonts) => PdfSharpConverter.Archive(path, changed: true, fx.NewFolder(), Ct, fonts);

    private static PdfDictionary Font(PdfDocument document, string name) =>
        (PdfDictionary)V(document.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements["/" + name])!;

    private static PdfDictionary Descriptor(PdfDictionary font) => (PdfDictionary)V(font.Elements["/FontDescriptor"])!;

    private static string Text(string path)
    {
        using var pig = PigDocument.Open(path);
        return string.Concat(pig.GetPage(1).Text.Where(c => !char.IsWhiteSpace(c)));
    }

    // ---- fonts

    [Fact]
    public void A_font_that_is_not_embedded_is_embedded_from_an_installed_one_with_its_widths()
    {
        var path = Page(Path.Combine(fx.NewFolder(), "sans.pdf"), "BT /F1 24 Tf 72 700 Td (Hamburgefonstiv) Tj ET",
            "/Font << /F1 5 0 R /F2 6 0 R >>", extra: [SansFont(), "<< /Type /Font /Subtype /Type1 /BaseFont /NeverDrawn >>"]);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.Sans));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var font = Font(document, "F1");
            Assert.Equal("/TrueType", font.Elements.GetName("/Subtype"));
            Assert.Matches("^/[A-Z]{6}\\+FileeTest-TrueType$", font.Elements.GetName("/BaseFont"));
            Assert.False(font.Elements.ContainsKey("/Encoding")); // a symbolic TrueType font
            Assert.True(font.Elements.ContainsKey("/ToUnicode"));
            var descriptor = Descriptor(font);
            Assert.Equal(4, descriptor.Elements.GetInteger("/Flags") & 4);
            var program = ((PdfDictionary)V(descriptor.Elements["/FontFile2"])!).Stream!.UnfilteredValue;
            Assert.True(program.Length < 20_000, $"{program.Length} bytes: a subset");
            // A font listed but never drawn needs no program.
            Assert.False(Font(document, "F2").Elements.ContainsKey("/FontDescriptor"));
        }
        Assert.Equal("Hamburgefonstiv", Text(path));
    }

    [Fact]
    public void A_font_whose_installed_namesake_has_other_widths_is_refused_with_its_name()
    {
        // The test font answers for Helvetica, but its glyphs are Noto Sans's, not Helvetica's widths.
        var path = Page(Path.Combine(fx.NewFolder(), "helvetica.pdf"), "BT /F1 24 Tf 72 700 Td (Hamburgefonstiv) Tj ET",
            "/Font << /F1 5 0 R >>", extra: ["<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"]);
        var bytes = File.ReadAllBytes(path);

        var refused = Assert.Throws<ConversionRefusedException>(() => Archive(path, PdfATestFonts.As("Helvetica", "Arial")));

        Assert.Equal(PdfAConformance.FontNotEmbedded, refused.ErrorKey);
        Assert.Equal("Helvetica", refused.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("Helvetica", "Arial", "Liberation Sans")]
    [InlineData("Times-Bold", "Times New Roman", "Liberation Serif")]
    [InlineData("Courier-Oblique", "Courier New", "Liberation Mono")]
    public void A_standard_font_gets_its_installed_metric_compatible_replacement(string standard, string windows, string linux)
    {
        Assert.SkipUnless(SystemFonts.Instance.Family(windows).Count > 0 || SystemFonts.Instance.Family(linux).Count > 0,
            $"Neither {windows} nor {linux} is installed");
        var path = Page(Path.Combine(fx.NewFolder(), "standard.pdf"), "BT /F1 18 Tf 72 700 Td (Hamburgefonstiv ÁáéÅçÑü 0123) Tj ET",
            "/Font << /F1 5 0 R >>", extra: [$"<< /Type /Font /Subtype /Type1 /BaseFont /{standard} /Encoding /WinAnsiEncoding >>"]);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, SystemFonts.Instance));

        PdfA2bChecks.Assert2b(path);
        Assert.Equal("HamburgefonstivÁáéÅçÑü0123", Text(path));
    }

    /// <summary>A Type 0 font with an Identity encoding, a ToUnicode map and no program, drawing CIDs 1 and 2 (H and i).</summary>
    internal static string Composite(string path, string widths = "", string cmapBody = "2 beginbfchar <0001> <0048> <0002> <0069> endbfchar")
    {
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            cmapBody + " endcmap CMapName currentdict /CMap defineresource pop end end";
        return Page(path, "BT /F1 24 Tf 72 700 Td <00010002> Tj ET", "/Font << /F1 5 0 R >>", extra:
        [
            "<< /Type /Font /Subtype /Type0 /BaseFont /FileeSans /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FileeSans /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/DW 1000 /W [{(widths.Length > 0 ? widths : $"1 [{PdfATestFonts.Advance('H')} {PdfATestFonts.Advance('i')}]")}] >>",
            RawPdf.Stream("", cmap),
        ]);
    }

    [Fact]
    public void A_composite_font_that_is_not_embedded_maps_its_cids_through_its_text()
    {
        var path = Composite(Path.Combine(fx.NewFolder(), "cid.pdf"));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.Sans));

        PdfA2bChecks.Assert2b(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var cidFont = (PdfDictionary)V(((PdfArray)V(Font(document, "F1").Elements["/DescendantFonts"])!).Elements[0])!;
            Assert.Equal("/CIDFontType2", cidFont.Elements.GetName("/Subtype"));
            Assert.NotNull(((PdfDictionary)V(cidFont.Elements["/CIDToGIDMap"])!).Stream); // CID 1 is not glyph 1
        }
        Assert.Equal("Hi", Text(path));
    }

    [Fact]
    public void Font_entries_built_to_be_costly_finish_quickly()
    {
        // Widths for every CID there is, twice over, and a ToUnicode range over every two-byte code.
        var path = Composite(Path.Combine(fx.NewFolder(), "costly.pdf"), widths: "0 2000000000 500 0 4000000000 [1 2 3]",
            cmapBody: "2 beginbfrange <0000> <FFFF> <0041> <00000000> <FFFFFFFF> <0042> endbfrange");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // The test font's H and i aren't 500 wide: refused, as any font whose widths aren't its own.
        var refused = Assert.Throws<ConversionRefusedException>(() => Archive(path, PdfATestFonts.Sans));

        Assert.Equal(PdfAConformance.FontNotEmbedded, refused.ErrorKey);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{stopwatch.Elapsed}");
    }

    // ---- annotations

    /// <summary>Every kind of annotation Filee draws an appearance for, none with one of its own.</summary>
    internal static string Annotated(string path)
    {
        string Widget(string entries, string name) => $"<< /Type /Annot /Subtype /Widget {entries} /NM ({name}) >>";
        string Markup(string subtype, string entries, string name) => $"<< /Type /Annot /Subtype /{subtype} {entries} /NM ({name}) >>";
        var quads = "/QuadPoints [300 580 400 580 300 565 400 565]";
        var annotations = string.Join(' ', new[]
        {
            Markup("Square", "/Rect [300 700 360 740] /C [1 0 0] /IC [0 1 0] /BS << /W 2 /S /D /D [3 2] >>", "square"),
            Markup("Circle", "/Rect [370 700 430 740] /C [0 0 1]", "circle"),
            Markup("Line", "/Rect [300 650 400 690] /L [300 650 400 690] /C [0 0 0]", "line"),
            Markup("PolyLine", "/Rect [410 650 500 690] /Vertices [410 650 450 690 500 650] /C [1 0 0]", "polyline"),
            Markup("Polygon", "/Rect [510 650 600 690] /Vertices [510 650 550 690 600 650] /C [0 0 0] /IC [0 0 0 0.3]", "polygon"),
            Markup("Ink", "/Rect [300 600 340 640] /InkList [[300 600 320 640 340 600]] /C [0 0.5 0]", "ink"),
            Markup("Highlight", $"/Rect [300 565 400 580] {quads} /C [1 1 0]", "highlight"),
            Markup("Underline", $"/Rect [300 565 400 580] {quads}", "underline"),
            Markup("StrikeOut", $"/Rect [300 565 400 580] {quads} /C [1 0 0]", "strikeout"),
            Markup("Squiggly", $"/Rect [300 565 400 580] {quads}", "squiggly"),
            Markup("Caret", "/Rect [410 565 420 580]", "caret"),
            Markup("Text", "/Rect [430 560 450 580] /C [1 0.8 0] /Contents (A note)", "note"),
            Markup("FreeText", "/Rect [400 300 480 350] /DA (/FSans 9 Tf 0 g) /Contents (Hi Hamburg Hi Hamburg)", "free"),
            Markup("Link", "/Rect [72 200 200 220] /Border [0 0 1] /C [0 0 1] /A << /S /URI /URI (https://example.com) >>", "link"),
            Markup("Link", "/Rect [72 170 200 190] /Border [0 0 0] /A << /S /URI /URI (https://example.com) >>", "plainlink"),
            Markup("Square", "/Rect [5 5 5 5] /C [1 0 0]", "zero"),
            Markup("Popup", "/Rect [450 450 550 500]", "popup"),
        }) + " 6 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R 14 0 R";
        return Page(path, "", "", $"/Annots [{annotations}]",
            "/AcroForm << /Fields [6 0 R 7 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R 14 0 R] /DR << /Font << /FSans 5 0 R >> >> /DA (/FSans 12 Tf 0 g) >>",
        [
            /* 5 */ SansFont(),
            /* 6 */ Widget("/FT /Btn /T (check) /V /Yes /Rect [72 500 90 518] /MK << /BC [0 0 0] /BG [1 1 1] >> /DA (/FSans 0 Tf 0 g)", "check"),
            /* 7 */ "<< /FT /Btn /Ff 49152 /T (radio) /V /B /Kids [8 0 R] >>",
            /* 8 */ Widget("/Parent 7 0 R /Rect [100 500 118 518] /AS /B /MK << /BC [0 0 1] >>", "radio"),
            /* 9 */ Widget("/FT /Tx /T (name) /V (Hi Hamburg) /Rect [72 450 272 470] /DA (/FSans 10 Tf 0 0 1 rg) /Q 1 /MK << /BC [0 0 0] >>", "text"),
            /* 10 */ Widget("/FT /Ch /Ff 131072 /T (combo) /V (Hi) /Opt [(Hi) (Ho)] /Rect [72 400 172 420]", "combo"),
            /* 11 */ Widget("/FT /Ch /T (list) /V (Ho) /Opt [(Hi) (Ho)] /Rect [72 340 172 390] /MK << /BC [0 0 0] >>", "list"),
            /* 12 */ Widget("/FT /Sig /T (signature) /Rect [72 300 172 330]", "signature"),
            /* 13 */ Widget("/FT /Btn /Ff 65536 /T (push) /MK << /CA (Hi) /BG [0.8 0.8 0.8] >> /Rect [200 500 260 520]", "push"),
            /* 14 */ Widget("/FT /Tx /Ff 8192 /T (password) /V (secret) /Rect [280 500 380 520]", "password"),
        ]);
    }

    [Fact]
    public void Annotations_without_appearances_get_ones_drawn_from_their_entries()
    {
        var path = Annotated(Path.Combine(fx.NewFolder(), "annotations.pdf"));
        Assert.Contains(PdfA2bChecks.Check(File.ReadAllBytes(path)), b => b.StartsWith("6.3.3", StringComparison.Ordinal));

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.Sans));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annotations = ((PdfArray)V(document.Pages[0].Elements["/Annots"])!).Elements.Select(V).Cast<PdfDictionary>()
            .ToDictionary(a => a.Elements.GetString("/NM"));
        PdfDictionary? Normal(string name) => V(annotations[name].Elements.GetDictionary("/AP")?.Elements["/N"]) as PdfDictionary;
        foreach (var name in new[] { "square", "circle", "line", "polyline", "polygon", "ink", "highlight", "underline", "strikeout", "squiggly",
                     "caret", "note", "free", "link", "text", "combo", "list", "signature", "password" })
        {
            Assert.True(Normal(name)?.Stream is not null, name);
            Assert.Equal(4, annotations[name].Elements.GetInteger("/F") & 4);
        }
        foreach (var name in new[] { "plainlink", "zero", "popup" })
            Assert.Null(Normal(name));
        Assert.Equal(["/Off", "/Yes"], Normal("check")!.Elements.Keys.Order());
        Assert.Equal("/Yes", annotations["check"].Elements.GetName("/AS"));
        Assert.Equal(["/B", "/Off"], Normal("radio")!.Elements.Keys.Order());
        Assert.Equal("/B", annotations["radio"].Elements.GetName("/AS"));
        Assert.Equal(["/Off"], Normal("push")!.Elements.Keys);

        // The drawings: shapes in their colours, text in the field's font, which is embedded now.
        string Content(string name) => Encoding.Latin1.GetString(Normal(name)!.Stream!.UnfilteredValue);
        Assert.Contains("1 0 0 RG", Content("square"));
        Assert.Contains("0 1 0 rg", Content("square"));
        Assert.Contains("[3 2] 0 d", Content("square"));
        Assert.Contains(" c", Content("circle"));
        Assert.Contains("/G0 gs", Content("highlight"));
        Assert.Contains("(Hi Hamburg) Tj", Content("text"));
        Assert.Contains("0 0 1 rg", Content("text"));
        Assert.DoesNotContain("secret", Content("password"));
        Assert.Contains("(Hi Hamburg", Content("free"));
        var font = (PdfDictionary)V(Normal("text")!.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements["/F0"])!;
        Assert.True(Descriptor(font).Elements.ContainsKey("/FontFile2"));
        // The polygon's CMYK fill made the page device independent.
        var spaces = Normal("polygon")!.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ColorSpace")!;
        Assert.True(spaces.Elements.ContainsKey("/DefaultCMYK"));
    }

    [Fact]
    public void A_free_text_helvetica_cannot_encode_is_still_refused()
    {
        var path = Page(Path.Combine(fx.NewFolder(), "korean.pdf"), "", annotations:
            "/Annots [<< /Type /Annot /Subtype /FreeText /Rect [10 10 150 50] /DA (/Helv 12 Tf 0 g) /Contents <FEFFD55CAE00> >>]");

        var refused = Assert.Throws<ConversionRefusedException>(() => Archive(path, PdfATestFonts.Sans));

        Assert.Equal(PdfAConformance.NoAppearance, refused.ErrorKey);
    }

    // ---- colours

    [Fact]
    public void Device_cmyk_without_a_cmyk_intent_gets_a_default_cmyk_space_and_the_srgb_intent()
    {
        var path = Page(Path.Combine(fx.NewFolder(), "cmyk.pdf"),
            "0 0 0 1 k 72 600 200 100 re f 1 0 0 rg 0 0 10 10 re f q 100 0 0 100 300 300 cm /Im1 Do Q /Fm1 Do " +
            "q 10 0 0 10 400 400 cm BI /W 1 /H 1 /CS /CMYK /BPC 8 ID \u0000\u0080\u0000\u0000 EI Q",
            "/XObject << /Im1 5 0 R /Fm1 6 0 R >>", extra:
        [
            RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8",
                "\u0000ÿÿ\u0000ÿ\u0000\u0000\u0000"),
            RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "0 1 0 0 k 0 0 5 5 re f"),
        ]);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var intent = (PdfDictionary)V(document.Internals.Catalog.Elements.GetArray("/OutputIntents")!.Elements.Single())!;
        Assert.Equal(SrgbProfile.Name, intent.Elements.GetString("/OutputConditionIdentifier"));
        var resources = document.Pages[0].Elements.GetDictionary("/Resources")!;
        var space = (PdfArray)V(resources.Elements.GetDictionary("/ColorSpace")!.Elements["/DefaultCMYK"])!;
        Assert.Equal("/ICCBased", ((PdfName)space.Elements[0]).Value);
        var profile = (PdfDictionary)V(space.Elements[1])!;
        Assert.Equal(4, profile.Elements.GetInteger("/N"));
        Assert.Equal(CmykProfile.Bytes, profile.Stream!.UnfilteredValue);
        var xobjects = resources.Elements.GetDictionary("/XObject")!;
        var image = (PdfDictionary)V(xobjects.Elements["/Im1"])!;
        Assert.Same(space, V(image.Elements["/ColorSpace"]));
        var form = (PdfDictionary)V(xobjects.Elements["/Fm1"])!;
        Assert.Same(space, V(form.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ColorSpace")!.Elements["/DefaultCMYK"]));
    }

    [Fact]
    public void Device_rgb_beside_a_cmyk_intent_gets_a_default_rgb_space()
    {
        var path = Page(Path.Combine(fx.NewFolder(), "press.pdf"), "0 0 0 1 k 72 600 200 100 re f 1 0 0 rg 0 0 10 10 re f",
            catalog: "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) /DestOutputProfile 5 0 R >>]",
            extra: [RawPdf.Stream("/N 4", Encoding.Latin1.GetString(PdfAConformanceTests.IccProfile("prtr", "CMYK")))]);

        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));

        PdfA2bChecks.Assert2b(path);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var intent = (PdfDictionary)V(document.Internals.Catalog.Elements.GetArray("/OutputIntents")!.Elements.Single())!;
        Assert.Equal("FOGRA39", intent.Elements.GetString("/OutputConditionIdentifier"));
        var space = (PdfArray)V(document.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ColorSpace")!.Elements["/DefaultRGB"])!;
        Assert.Equal(SrgbProfile.Bytes, ((PdfDictionary)V(space.Elements[1])!).Stream!.UnfilteredValue);
    }

    [Fact]
    public void The_cmyk_profile_is_a_version_2_cmyk_input_profile_of_the_plain_conversion()
    {
        var icc = CmykProfile.Bytes;
        var broken = new List<string>();
        PdfA2bChecks.CheckIcc(icc, 4, intent: false, "cmyk", broken);
        Assert.Empty(broken);
        Assert.Equal("scnr", Encoding.ASCII.GetString(icc, 12, 4));
        Assert.Equal("CMYK", Encoding.ASCII.GetString(icc, 16, 4));
        Assert.Equal("Lab ", Encoding.ASCII.GetString(icc, 20, 4));
        Assert.True(icc.Length < 16_000, $"{icc.Length} bytes");
        Assert.Equal(icc, CmykProfile.Bytes);

        var white = CmykProfile.ToLab(1, 1, 1);
        Assert.Equal(100, white.L, 0.05);
        Assert.Equal(0, white.A, 0.05);
        Assert.Equal(0, white.B, 0.05);
        var (r, g, b) = CmykProfile.ToRgb(1, 0, 0, 0);
        Assert.Equal((0.0, 1.0, 1.0), (r, g, b));
        var black = CmykProfile.ToLab(0, 0, 0);
        Assert.Equal(0, black.L, 0.05);
    }

    // ---- the checker

    /// <summary>A PDF/A Filee made, for the checker to find what was broken in it.</summary>
    private string Good()
    {
        var path = Page(Path.Combine(fx.NewFolder(), "good.pdf"), "BT /F1 12 Tf 72 700 Td (Hi) Tj ET", "/Font << /F1 5 0 R >>",
            extra: [PdfATestFonts.TrueTypeFont(6), PdfATestFonts.Descriptor("FileeTest-TrueType", 7), PdfATestFonts.Program()]);
        Assert.Equal(PdfArchiveOutcome.Archived, Archive(path, PdfATestFonts.None));
        PdfA2bChecks.Assert2b(path);
        return path;
    }

    public static TheoryData<string, string> Breakages() => new()
    {
        { "xref", "6.1.4 object" },
        { "length", "6.1.7.1 Length" },
        { "pdfaid", "6.6.4 pdfaid" },
        { "date", "6.6.3 /ModDate" },
    };

    [Theory]
    [MemberData(nameof(Breakages))]
    public void The_checker_finds_a_broken_update_or_metadata(string kind, string rule)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(Good()));
        // The incremental update: after the %%EOF of what PDFsharp saved.
        var update = text.IndexOf("%%EOF", StringComparison.Ordinal);
        text = kind switch
        {
            // The update's first entry one byte off.
            "xref" => text[..update] + XrefOffset().Replace(text[update..], m => (long.Parse(m.Groups[1].Value) + 1).ToString("D10") + m.Groups[2].Value, 1),
            "length" => MetadataLength().Replace(text, m => $"/Subtype/XML/Length {int.Parse(m.Groups[1].Value) + 1}>>", 1, update),
            "pdfaid" => text.Replace("<pdfaid:part>2<", "<pdfaid:part>3<", StringComparison.Ordinal),
            _ => ModYear().Replace(text, m => $"/ModDate (D:{int.Parse(m.Groups[1].Value) - 1}", 1),
        };

        var broken = PdfA2bChecks.Check(Encoding.Latin1.GetBytes(text));

        Assert.Contains(broken, b => b.StartsWith(rule, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(\d{10})( \d{5} n\r\n)")]
    private static partial Regex XrefOffset();

    [GeneratedRegex(@"/Subtype/XML/Length (\d+)>>")]
    private static partial Regex MetadataLength();

    [GeneratedRegex(@"/ModDate \(D:(\d{4})")]
    private static partial Regex ModYear();

    public static TheoryData<string, string> Faults() => new()
    {
        { "widths", "6.2.11.5 width" },
        { "symbolic", "6.2.11.6 symbolic" },
        { "notdef", "6.2.11.8" },
        { "javascript", "6.5.1" },
        { "inline-lzw", "6.1.7.2 inline" },
        { "cmyk", "6.2.4.3 DeviceCMYK in content" },
        { "icc", "6.2.3 profile: no desc" },
        { "metadata", "6.6.2.1 2 metadata streams" },
        { "not-embedded", "6.2.11.4.1" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void The_checker_finds_what_pdf_a_forbids(string kind, string rule)
    {
        var path = Path.Combine(fx.NewFolder(), kind + ".pdf");
        string FontPage(string content, string? font = null, int flags = 32) => Page(path, content, "/Font << /F1 5 0 R >>",
            extra: [font ?? PdfATestFonts.TrueTypeFont(6), PdfATestFonts.Descriptor("FileeTest-TrueType", 7, flags), PdfATestFonts.Program()]);
        var icc = new byte[132];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(icc, 132);
        icc[8] = 2;
        Encoding.ASCII.GetBytes("mntrRGB XYZ ", icc.AsSpan(12));
        Encoding.ASCII.GetBytes("acsp", icc.AsSpan(36));
        _ = kind switch
        {
            "widths" => FontPage("BT /F1 12 Tf (Hi) Tj ET", PdfATestFonts.TrueTypeFont(6, extraWidth: 50)),
            "symbolic" => FontPage("BT /F1 12 Tf (Hi) Tj ET", flags: 4),
            "notdef" => FontPage("BT /F1 12 Tf (Zq) Tj ET"),
            "javascript" => Page(path, "", catalog: "/OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >>"),
            "inline-lzw" => Page(path, "BI /W 1 /H 1 /CS /G /BPC 8 /F /LZW ID \u0080\u000b`P EI"),
            "cmyk" => Page(path, "0 0 0 1 k 0 0 10 10 re f"),
            "icc" => Page(path, "", catalog: "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (x) /DestOutputProfile 5 0 R >>]",
                extra: [RawPdf.Stream("/N 3", Encoding.Latin1.GetString(icc))]),
            "metadata" => Page(path, "", catalog: "/Metadata 5 0 R", annotations: "/Metadata 6 0 R", extra:
                [RawPdf.Stream("/Type /Metadata /Subtype /XML", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"),
                 RawPdf.Stream("/Type /Metadata /Subtype /XML", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>")]),
            _ => Page(path, "BT /F1 12 Tf (Hi) Tj ET", "/Font << /F1 5 0 R >>", extra: [SansFont()]),
        };

        var broken = PdfA2bChecks.Check(File.ReadAllBytes(path));

        Assert.Contains(broken, b => b.StartsWith(rule, StringComparison.Ordinal));
    }

    // ---- converted PDFs

    private ConverterCatalog WithoutGhostscript() =>
        new(fx.Converters.Where(c => c.Id != "ghostscript")) { Priority = fx.Catalog.Priority };

    private static Preset PdfA() => new() { TargetFormat = "pdf", Document = { PdfA = true }, Output = { FileNamePattern = "{name}_pdfa" } };

    [Fact]
    public async Task Pictures_converted_to_pdf_come_out_pdf_a_rgb_and_cmyk_alike()
    {
        var dir = fx.NewFolder();
        var png = EngineFixture.MakeImage(dir, "picture.png", MagickFormat.Png);
        var tiff = Path.Combine(dir, "print.tiff");
        using (var image = new MagickImage(MagickColors.Orange, 60, 40))
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
            image.RemoveProfile("icc"); // device CMYK in the PDF
            image.Write(tiff);
        }

        var job = await fx.ConvertAsync([png, tiff], PdfA(), WithoutGhostscript());

        Assert.Equal(JobState.Completed, job.State);
        foreach (var file in job.Files)
        {
            Assert.Null(file.NoticeKey);
            PdfA2bChecks.Assert2b(file.Outputs.Single());
        }
        using var cmyk = PdfReader.Open(job.Files[1].Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.True(cmyk.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ColorSpace")?.Elements.ContainsKey("/DefaultCMYK"));
    }
}
