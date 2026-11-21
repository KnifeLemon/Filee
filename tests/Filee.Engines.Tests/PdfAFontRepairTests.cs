// PDF/A-2b of PDFs whose own TrueType fonts break the font rules (Pdf/PdfAFontRepair.cs): widths that differ from the
// program, a symbolic font with an encoding, a non-symbolic one with StandardEncoding, no ToUnicode, a CIDFontType2
// with other widths; composite fonts with a predefined Unicode CMap (UniKS-UCS2-H, UniJIS-UCS2-H) embedded from the
// computer's CJK fonts; and the width check of PdfAFonts against Windows' and Liberation's replacements of the standard
// fonts. The PDFs are written in code (RawPdf, PdfATestFonts' program); tests of installed fonts skip without them.

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

    // ---- predefined Unicode CMaps

    /// <summary>A page drawing UTF-16 <paramref name="text"/> with a CID font that isn't embedded, through a predefined CMap.</summary>
    private static string PredefinedPage(string path, string text, string cmap, string baseFont, string ordering) =>
        PdfAEmbeddingTests.Page(path, $"BT /F1 24 Tf 72 700 Td <{Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(text))}> Tj ET",
            "/Font << /F1 5 0 R >>", extra:
            [
                $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont}-{cmap} /Encoding /{cmap} /DescendantFonts [6 0 R] >>",
                $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 2 >> " +
                    "/DW 1000 /W [1 [500]] /FontDescriptor 7 0 R >>",
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
