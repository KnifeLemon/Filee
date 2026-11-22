// How high the layout engine makes the lines of Word documents (ParagraphLayout.WordLines.cs): Windows' external
// leading, East Asian fonts' extra leading, a named East Asian font that isn't installed, symbol fonts, "at least"
// spacing, display equations with "multiple" spacing and the document grid (issue #356). Laid out with the Noto fonts
// of build/fetch-fonts.ps1 (RepositoryFonts), so every computer measures the same; without them the tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordLineHeightLayoutTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>Noto Sans for Latin text, Noto Sans SC for Chinese, 10 pt, single spacing, no space after paragraphs.</summary>
    private const string Styles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Noto Sans" w:hAnsi="Noto Sans" w:eastAsia="Noto Sans SC" w:cs="Noto Sans"/>
        <w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblCellMar>
          <w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        """;

    /// <summary>An 18 pt line grid, as Japanese and Korean templates have.</summary>
    private const string Grid = DefaultPage + "<w:docGrid w:type=\"lines\" w:linePitch=\"360\"/>";

    private const string Fraction = "<m:oMathPara><m:oMath><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num>" +
                                    "<m:den><m:r><m:t>b</m:t></m:r></m:den></m:f></m:oMath></m:oMathPara>";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Fonts_for_east_asian_code_pages_are_told_apart()
    {
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Chinese);

        Assert.True(_shaper.Metrics(_fonts.Font(RepositoryFonts.Chinese)).EastAsian);
        Assert.True(_shaper.Metrics(_fonts.Font(RepositoryFonts.Korean)).EastAsian);
        Assert.False(_shaper.Metrics(_fonts.Font(RepositoryFonts.Latin)).EastAsian);
        Assert.False(_shaper.Metrics(_fonts.Font(RepositoryFonts.Latin)).Math);
    }

    [Fact]
    public async Task East_asian_text_gets_three_tenths_more_half_above_it()
    {
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Chinese);
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }
            .Paragraph(P(R("中文第一行")) + P(R("中文第二行")) + P(R("Latin one")) + P(R("Latin two"))));

        var chinese = WinHeight(RepositoryFonts.Chinese, 10);
        Assert.Equal(1.3f * chinese, Baseline(page, "中文第二行") - Baseline(page, "中文第一行"), 0.05f);
        // Half the extra leading above the text: the first baseline is that much lower than the font's ascent.
        Assert.Equal(72 + 0.15f * chinese + WinAscent(RepositoryFonts.Chinese, 10), Baseline(page, "中文第一行"), 0.05f);
        // Latin fonts keep their Win metrics.
        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), Baseline(page, "Latin two") - Baseline(page, "Latin one"), 0.05f);
    }

    [Fact]
    public async Task Issue356_simsun_keeps_words_height_where_another_font_draws_it()
    {
        // 12 pt SimSun at 1.5 lines is 23.4 pt in Word (1.3 × 12 × 1.5). SimSun isn't installed here: Noto Sans SC, 1.45 em
        // high, draws it at SimSun's height.
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Chinese);
        const string SimSun = "<w:rFonts w:eastAsia=\"SimSun\"/><w:sz w:val=\"24\"/>";
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(
            P(R("虚拟同步机", SimSun), "<w:spacing w:line=\"360\" w:lineRule=\"auto\"/>") +
            P(R("功角动态由", SimSun), "<w:spacing w:line=\"360\" w:lineRule=\"auto\"/>")));

        Assert.Equal(23.4f, Baseline(page, "功角动态由") - Baseline(page, "虚拟同步机"), 0.05f);
    }

    [Fact]
    public async Task Issue356_a_display_equation_adds_the_marks_share_of_the_multiple_not_its_own()
    {
        // Word's 1.5 lines add half the paragraph mark's line to a display equation, as to a picture: two equation
        // systems on page 2 of #356 are 16 pt apart in Word, not half their height more.
        NeedFonts(RepositoryFonts.Latin);
        async Task<float> Span(string line)
        {
            var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(
                P(R("Before")) + P(Fraction, $"<w:spacing w:line=\"{line}\" w:lineRule=\"auto\"/>") + P(R("After"))));
            return Baseline(page, "After") - Baseline(page, "Before");
        }

        var single = await Span("240");
        Assert.Equal(single + WinHeight(RepositoryFonts.Latin, 10) / 2, await Span("360"), 0.1f);
    }

    [Fact]
    public async Task Lines_snap_to_the_document_grid_unless_the_paragraph_says_not_to()
    {
        // 10 pt Noto Sans is 15.2 pt high: one line of an 18 pt grid. 1.5 lines are 1.5 grid lines.
        NeedFonts(RepositoryFonts.Latin);
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles, SectionProperties = Grid }.Paragraph(
            P(R("Grid one")) + P(R("Grid two")) +
            P(R("Half one"), "<w:spacing w:line=\"360\" w:lineRule=\"auto\"/>") + P(R("Half two"), "<w:spacing w:line=\"360\" w:lineRule=\"auto\"/>") +
            P(R("Free one"), "<w:snapToGrid w:val=\"0\"/>") + P(R("Free two"), "<w:snapToGrid w:val=\"0\"/>")));

        Assert.Equal(18, Baseline(page, "Grid two") - Baseline(page, "Grid one"), 0.05f);
        Assert.Equal(27, Baseline(page, "Half two") - Baseline(page, "Half one"), 0.05f);
        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), Baseline(page, "Free two") - Baseline(page, "Free one"), 0.05f);
        // The text sits in the middle of its grid line.
        Assert.Equal(72 + (18 - WinHeight(RepositoryFonts.Latin, 10)) / 2 + WinAscent(RepositoryFonts.Latin, 10), Baseline(page, "Grid one"), 0.05f);
    }

    [Fact]
    public async Task Table_cells_snap_to_the_grid_only_where_the_settings_say_so()
    {
        NeedFonts(RepositoryFonts.Latin);
        const string Table = "<w:tbl><w:tblPr><w:tblW w:w=\"4000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid>" +
                             "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4000\" w:type=\"dxa\"/></w:tcPr><w:p><w:r><w:t>Cell one</w:t></w:r></w:p>" +
                             "<w:p><w:r><w:t>Cell two</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
        async Task<float> Pitch(string? settings)
        {
            var page = await LayoutAsync(new DocxBuilder { Styles = Styles, SectionProperties = Grid, Settings = settings }.Paragraph(Table + P("")));
            return Baseline(page, "Cell two") - Baseline(page, "Cell one");
        }

        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), await Pitch(null), 0.05f);
        Assert.Equal(18, await Pitch("<w:compat><w:adjustLineHeightInTable/></w:compat>"), 0.05f);
    }

    [Fact]
    public async Task A_unicode_symbol_does_not_make_its_line_taller_than_the_text()
    {
        // ☑ typed as Unicode and drawn in a fallback font, often a much taller one: the line keeps the text's height.
        NeedFonts(RepositoryFonts.Latin);
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(R("☑", "<w:sz w:val=\"40\"/>") + R("Checked")) + P(R("Next"))));

        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), Baseline(page, "Next") - Baseline(page, "Checked"), 0.05f);
    }

    [Fact]
    public async Task A_symbol_from_a_symbol_font_counts_with_that_fonts_height()
    {
        // ☑ from Wingdings 2's F052 at 20 pt: Word measures Wingdings 2 (Win ascent 1727, descent 432 of 2048), drawn in it
        // where it is installed and with its metrics where another font draws ☑. Before, the line had the text's height.
        NeedFonts(RepositoryFonts.Latin);
        const string Box = "<w:r><w:rPr><w:sz w:val=\"40\"/></w:rPr><w:sym w:font=\"Wingdings 2\" w:char=\"F052\"/></w:r>";
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(Box + R("Checked")) + P(R("Next"))));

        Assert.Equal(72 + 1727 * 20 / 2048f, Baseline(page, "Checked"), 0.05f);
        Assert.Equal(432 * 20 / 2048f + WinAscent(RepositoryFonts.Latin, 10), Baseline(page, "Next") - Baseline(page, "Checked"), 0.05f);
    }

    [Fact]
    public async Task A_sym_is_drawn_and_measured_in_its_own_font_where_it_is_installed()
    {
        // w:sym w:font names the font Word draws the code in; the run's text font is SimSun here (not installed: an East
        // Asian font measured at Word's 1.3 em), which the © must not take its height from.
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Chinese);
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(
            "<w:r><w:rPr><w:rFonts w:ascii=\"SimSun\" w:hAnsi=\"SimSun\"/></w:rPr><w:sym w:font=\"Noto Sans\" w:char=\"00A9\"/></w:r>" + R(" 2026")) +
            P(R("Next"))));

        var run = page.Items.OfType<GlyphItem>().Select(i => i.Run).First(r => r.Text.Contains('©', StringComparison.Ordinal));
        Assert.Equal(RepositoryFonts.Latin, run.Font.Typeface.FamilyName);
        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), Baseline(page, "Next") - Baseline(page, "©"), 0.02f);
    }

    [Fact]
    public async Task Wingdings_codes_are_drawn_in_wingdings_where_it_is_installed()
    {
        Assert.SkipUnless(SystemFonts.Instance.Family("Wingdings 2").Count > 0 && SystemFonts.Instance.Family("Wingdings").Count > 0,
            "Wingdings isn't installed");
        // "Wingdings2" (as PDF converters write it) is Wingdings 2; "WP Wingdings" with altName Wingdings is Wingdings.
        var docx = new DocxBuilder
        {
            Styles = Styles,
            FontTable = "<w:font w:name=\"WP Wingdings\"><w:altName w:val=\"Wingdings\"/><w:charset w:val=\"02\"/></w:font>",
        }.Paragraph(P(
            "<w:r><w:sym w:font=\"Wingdings 2\" w:char=\"F052\"/></w:r>" + R(" one ") +
            R("R", "<w:rFonts w:ascii=\"Wingdings2\" w:hAnsi=\"Wingdings2\"/>") + R(" two ") +
            R("þ", "<w:rFonts w:ascii=\"WP Wingdings\" w:hAnsi=\"WP Wingdings\"/>") + R(" three")));
        var path = docx.Save(Path.Combine(fx.NewFolder(), "case.docx"));
        var document = await DocumentReaders.ReadAsync(path, "docx", fx.NewFolder(), Token);
        var page = new PageComposer(new FontResolver(), _shaper, document.Kinsoku, StyleResolver.FromTemplate()).Compose(document, Token).Pages[0];

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).Where(r => r.Text == "☑").ToList();
        Assert.Equal(["Wingdings 2", "Wingdings 2", "Wingdings"], runs.Select(r => r.Font.Typeface.FamilyName));
        // Each a real glyph of the font (not .notdef), its text still ☑ for copying and search.
        Assert.All(runs, r => Assert.NotEqual(0, r.Glyphs[0]));
    }

    [Fact]
    public void Windows_external_leading_goes_above_latin_text()
    {
        // Times New Roman's metrics: Win 1825 + 443, hhea line gap 87 of 2048 units, all of it external leading. Word's
        // single line of 12 pt Times New Roman is 13.8 pt, Win metrics alone 13.3.
        var times = new FontMetrics(2048, 1825, -443, 87, 1825, 443, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ExternalLeading: 87);
        var style = new TextStyle("Times New Roman", FontStyle.Regular, 12, 1, 0, 0, 12, SKColors.Black, null, false, false, "");
        var (ascent, descent) = ParagraphLayouter.WordExtent(style, Face("Times New Roman"), times);
        Assert.Equal(13.8f, ascent + descent, 0.005f);
        Assert.Equal((1825 + 87) * 12 / 2048f, ascent, 0.001f);

        // An East Asian font has Word's 30 % leading instead of its line gap (SimSun: 1.0 em Win, 0.14 em gap).
        var simSun = new FontMetrics(256, 220, -36, 36, 220, 36, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, EastAsian: true, ExternalLeading: 36);
        var (a, d) = ParagraphLayouter.WordExtent(style with { Family = "SimSun" }, Face("SimSun"), simSun);
        Assert.Equal(15.6f, a + d, 0.005f);

        static ResolvedFont Face(string family) => new(new FontFace(family, 400, false, _ => true));
    }

    [Fact]
    public void External_leading_is_read_from_the_font_tables()
    {
        NeedFonts(RepositoryFonts.Latin);
        // Noto Sans: hhea line gap 0.
        Assert.Equal(0, _shaper.Metrics(_fonts.Font(RepositoryFonts.Latin)).ExternalLeading);
        Assert.SkipUnless(SystemFonts.Instance.Family("Times New Roman") is [_, ..], "Times New Roman isn't installed");
        var metrics = _shaper.Metrics(FontResolver.Match(SystemFonts.Instance.Family("Times New Roman"), FontStyle.Regular).Face.Font);
        Assert.Equal(87 * metrics.UnitsPerEm / 2048, metrics.ExternalLeading);
    }

    [Fact]
    public async Task At_least_compares_with_the_fonts_line_not_its_size()
    {
        // 10 pt Noto Sans is 15.2 pt high: "at least 10 pt" keeps that height (it was 10 pt, the font size); "at least
        // 20 pt" is 20 pt.
        NeedFonts(RepositoryFonts.Latin);
        async Task<float> Pitch(int twips)
        {
            var spacing = $"<w:spacing w:line=\"{twips}\" w:lineRule=\"atLeast\"/>";
            var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(R("Least one"), spacing) + P(R("Least two"), spacing)));
            return Baseline(page, "Least two") - Baseline(page, "Least one");
        }

        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), await Pitch(200), 0.05f);
        Assert.Equal(20, await Pitch(400), 0.05f);
    }

    [Fact]
    public async Task Exact_and_at_least_lines_stand_their_text_on_the_line_s_bottom()
    {
        // The baseline is the line's height less the font's Win descent: an exact 20 pt line of 10 pt Noto Sans has its
        // room above the text, an exact 8 pt line loses the tops of its glyphs above its top (it had them reach into the
        // line below), and an at-least line taller than its text stands on its bottom as an exact one; 한글's exact lines
        // keep their 85 % split (ParagraphLayoutTests).
        NeedFonts(RepositoryFonts.Latin);
        async Task<(float First, float Pitch)> Lines(string rule, int twips)
        {
            var spacing = $"<w:spacing w:line=\"{twips}\" w:lineRule=\"{rule}\"/>";
            var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(R("Line one"), spacing) + P(R("Line two"), spacing)));
            return (Baseline(page, "Line one") - 72, Baseline(page, "Line two") - Baseline(page, "Line one"));
        }

        var descent = WinHeight(RepositoryFonts.Latin, 10) - WinAscent(RepositoryFonts.Latin, 10);
        var (first, pitch) = await Lines("exact", 400);
        Assert.Equal(20 - descent, first, 0.05f);
        Assert.Equal(20, pitch, 0.05f);
        (first, pitch) = await Lines("exact", 160);
        Assert.Equal(8 - descent, first, 0.05f);
        Assert.Equal(8, pitch, 0.05f);
        (first, pitch) = await Lines("atLeast", 400);
        Assert.Equal(20 - descent, first, 0.05f);
        Assert.Equal(20, pitch, 0.05f);
        // An at-least line its text is taller than is the text's line, as before.
        (first, _) = await Lines("atLeast", 200);
        Assert.Equal(WinAscent(RepositoryFonts.Latin, 10), first, 0.05f);
    }

    [Fact]
    public async Task An_exact_line_too_small_for_its_text_cuts_off_the_glyph_tops_at_its_top()
    {
        // ECMA-376 17.3.1.33: a line too small for its text holds it at its bottom, "clipped from the top down". An exact
        // 8 pt line of 10 pt text clips its runs at its top; an exact 20 pt line, which holds its text, clips nothing.
        NeedFonts(RepositoryFonts.Latin);
        async Task<PageContent> Lines(int twips, string section = DefaultPage)
        {
            var spacing = $"<w:spacing w:line=\"{twips}\" w:lineRule=\"exact\"/>";
            var docx = new DocxBuilder { Styles = Styles, SectionProperties = section };
            return await LayoutAsync(docx.Paragraph(P(R("HHHH one"), spacing) + P(R("HHHH two"), spacing)));
        }
        static GlyphRun Run(PageContent page, string text) =>
            page.Items.OfType<GlyphItem>().Select(i => i.Run).First(r => r.Text.Contains(text, StringComparison.Ordinal));

        var small = await Lines(160);
        var descent = WinHeight(RepositoryFonts.Latin, 10) - WinAscent(RepositoryFonts.Latin, 10);
        var (one, two) = (Run(small, "one"), Run(small, "two"));
        Assert.Equal(72, one.Clip?.Top ?? float.NaN, 0.05f);
        Assert.Equal(80, two.Clip?.Top ?? float.NaN, 0.05f);
        Assert.Equal(two.Clip!.Value.Top + 8 - descent, two.Positions[0].Y, 0.05f);
        Assert.Null(Run(await Lines(400), "one").Clip);

        // Written vertically (tbRl) the clip turns with the text: the first line's top is the body's right edge, and no
        // ink of the sideways glyphs reaches past it (without the clip their tops did, by 3 pt).
        var vertical = await Lines(160, DefaultPage + "<w:textDirection w:val=\"tbRl\"/>");
        Assert.NotNull(Run(vertical, "one").ClipTransform);
        using var bitmap = vertical.Render(288);
        const float Scale = 4, Edge = 595.3f - 72;
        int Ink(float from, float to)
        {
            var count = 0;
            for (var x = (int)(from * Scale); x < (int)(to * Scale); x++)
            {
                for (var y = (int)(72 * Scale); y < (int)(300 * Scale); y++)
                    count += bitmap.GetPixel(x, y).Red < 128 ? 1 : 0;
            }
            return count;
        }
        Assert.True(Ink(Edge - 6, Edge) > 0, "no ink in the first line");
        Assert.Equal(0, Ink(Edge + 0.5f, Edge + 6));
    }

    [Fact]
    public async Task Headers_footers_and_notes_do_not_snap_to_the_grid()
    {
        // The grid sets the body's lines per page; headers, footers and notes keep their font's lines.
        NeedFonts(RepositoryFonts.Latin);
        var docx = new DocxBuilder { Styles = Styles };
        var header = docx.Header(P(R("Head one")) + P(R("Head two")));
        var footer = docx.Footer(P(R("Foot one")) + P(R("Foot two")));
        docx.Footnotes("<w:footnote w:id=\"1\">" + P(R("Note one")) + P(R("Note two")) + "</w:footnote>");
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" + Grid;
        var page = await LayoutAsync(docx.Paragraph(P(R("Body one") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>") + P(R("Body two"))));

        var line = WinHeight(RepositoryFonts.Latin, 10);
        Assert.Equal(18, Baseline(page, "Body two") - Baseline(page, "Body one"), 0.05f);
        Assert.Equal(line, Baseline(page, "Head two") - Baseline(page, "Head one"), 0.05f);
        Assert.Equal(line, Baseline(page, "Foot two") - Baseline(page, "Foot one"), 0.05f);
        Assert.Equal(line, Baseline(page, "Note two") - Baseline(page, "Note one"), 0.05f);
    }

    private void NeedFonts(params string[] families) =>
        Assert.SkipUnless(families.All(_fonts.Has), $"{string.Join(", ", families)} not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private async Task<PageContent> LayoutAsync(DocxBuilder docx)
    {
        var path = docx.Save(Path.Combine(fx.NewFolder(), "case.docx"));
        var document = await DocumentReaders.ReadAsync(path, "docx", fx.NewFolder(), Token);
        var pages = new PageComposer(new FontResolver(_fonts), _shaper, document.Kinsoku, StyleResolver.FromTemplate()).Compose(document, Token).Pages;
        return pages[0];
    }

    /// <summary>The baseline of the run that draws <paramref name="text"/>.</summary>
    private static float Baseline(PageContent page, string text) =>
        page.Items.OfType<GlyphItem>().Select(i => i.Run).First(r => r.Text.Contains(text, StringComparison.Ordinal)).Positions[0].Y;

    private float WinHeight(string family, float size)
    {
        var metrics = _shaper.Metrics(_fonts.Font(family));
        return (metrics.WinAscent + metrics.WinDescent) * size / metrics.UnitsPerEm;
    }

    private float WinAscent(string family, float size)
    {
        var metrics = _shaper.Metrics(_fonts.Font(family));
        return metrics.WinAscent * size / metrics.UnitsPerEm;
    }
}
