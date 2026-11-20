// How high the layout engine makes the lines of Word documents (ParagraphLayout.WordLines.cs): East Asian fonts' extra
// leading, a named East Asian font that isn't installed, display equations with "multiple" spacing and the document
// grid (issue #356). Laid out with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts), so every computer measures
// the same; without them the tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
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
    public async Task A_symbol_does_not_make_its_line_taller_than_the_text()
    {
        // ☑ (Wingdings 2's F052) read as Unicode and drawn in another font: Word measured the symbol font, no taller than
        // the text, so the line keeps the text's grid line.
        NeedFonts(RepositoryFonts.Latin);
        const string Box = "<w:r><w:rPr><w:rFonts w:ascii=\"Wingdings 2\" w:hAnsi=\"Wingdings 2\"/><w:sz w:val=\"40\"/></w:rPr><w:sym w:font=\"Wingdings 2\" w:char=\"F052\"/></w:r>";
        var page = await LayoutAsync(new DocxBuilder { Styles = Styles }.Paragraph(P(Box + R("Checked")) + P(R("Next"))));

        Assert.Equal(WinHeight(RepositoryFonts.Latin, 10), Baseline(page, "Next") - Baseline(page, "Checked"), 0.05f);
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
