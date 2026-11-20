// Word's table column widths in the layout engine (Typesetting/TableLayout.cs, issues #556, #1584): DOCX built in
// code, read by DocxReader and composed as the renderer composes Word documents. An autofit table widens a column to
// its longest word (a cell spanning columns widens them together) and to the table's preferred width, a fixed one
// keeps its grid, a share of the width is a share of the text column, and a nested table wider than its cell fits in
// it by giving up what its empty columns don't need. Laid out with the Noto fonts of build/fetch-fonts.ps1
// (RepositoryFonts); without them the layout tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordTableWidthTests : IDisposable
{
    /// <summary>The left margin of <see cref="DefaultPage"/> in points.</summary>
    private const float Margin = 72;

    /// <summary>Word's cell margin at each side (108 twips) in points.</summary>
    private const float CellMargin = 5.4f;

    /// <summary>The text width of <see cref="DefaultPage"/> (A4 less two inches) in points.</summary>
    private const float TextWidth = (11906 - 2880) / 20f;

    private const string LongWord = "Extraordinarily";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-word-tables").FullName;

    public void Dispose()
    {
        _shaper.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────────────────────── Autofit and fixed ─────────────────────────

    [Fact]
    public void An_autofit_table_widens_a_column_to_its_longest_word()
    {
        var page = ComposeOne(Table("", [600, 3000], [Cell(LongWord), Cell("Next")]));

        var word = Assert.Single(Runs(page), r => r.Text.Contains(LongWord, StringComparison.Ordinal));
        Assert.True(Left(page, "Next") > Margin + 30 + CellMargin + 1, "The column holding the long word grew past its grid width.");
        Assert.True(Left(page, "Next") > word.Positions[^1].X, "The next column starts after the word.");
    }

    [Fact]
    public void A_fixed_table_keeps_its_grid_and_breaks_the_word()
    {
        var page = ComposeOne(Table("<w:tblLayout w:type=\"fixed\"/>", [600, 3000], [Cell(LongWord), Cell("Next")]));

        Assert.DoesNotContain(Runs(page), r => r.Text.Contains(LongWord, StringComparison.Ordinal));
        Assert.Equal(Margin + 30 + CellMargin, Left(page, "Next"), 0.5f);
    }

    [Fact]
    public void A_cell_spanning_columns_widens_them_together()
    {
        var page = ComposeOne(Table("", [600, 600], [Cell(LongWord, "<w:gridSpan w:val=\"2\"/>")], [Cell("One"), Cell("Two")]));

        Assert.Single(Runs(page), r => r.Text.Contains(LongWord, StringComparison.Ordinal));
        var (one, two) = (Left(page, "One"), Left(page, "Two"));
        Assert.Equal(Margin + CellMargin, one, 0.5f);
        // Both columns took half of what the word needed beyond their 30 pt each.
        Assert.True(two - one > 30 + 5, $"The second column starts {two - one} pt after the first.");
    }

    // ───────────────────────── Preferred widths ─────────────────────────

    [Fact]
    public void An_autofit_table_widens_to_its_share_of_the_text_width()
    {
        var page = ComposeOne(Table("<w:tblW w:w=\"5000\" w:type=\"pct\"/>", [2000, 2000], [Cell("A"), Cell("B")]));

        Assert.Equal(Margin + TextWidth / 2 + CellMargin, Left(page, "B"), 0.5f);
    }

    [Fact]
    public void A_percentage_written_with_a_percent_sign_is_a_share_too()
    {
        var page = ComposeOne(Table("<w:tblW w:w=\"50%\" w:type=\"pct\"/>", [1000, 1000], [Cell("A"), Cell("B")]));

        Assert.Equal(Margin + TextWidth / 4 + CellMargin, Left(page, "B"), 0.5f);
    }

    [Fact]
    public void Cells_give_the_widths_the_grid_leaves_out()
    {
        var page = ComposeOne(Table("", [0, 0], [Cell("A", "<w:tcW w:w=\"1000\" w:type=\"dxa\"/>"), Cell("B", "<w:tcW w:w=\"3000\" w:type=\"dxa\"/>")],
            [Cell("C"), Cell("D")]));

        Assert.Equal(Margin + 50 + CellMargin, Left(page, "B"), 0.5f);
        Assert.Equal(Margin + 50 + CellMargin, Left(page, "D"), 0.5f);
    }

    // ───────────────────────── Nested tables ─────────────────────────

    [Fact]
    public void A_nested_table_wider_than_its_cell_fits_in_it_and_its_empty_column_gives_way()
    {
        // The outer cell is 150 pt; the nested table asks for 600 pt, half of it for an empty column.
        var nested = Table("<w:tblLayout w:type=\"fixed\"/><w:tblW w:w=\"12000\" w:type=\"dxa\"/>", [6000, 6000],
            [Cell("Alpha Beta Gamma", "<w:tcW w:w=\"20000\" w:type=\"dxa\"/>"), Cell("", "<w:tcW w:w=\"20000\" w:type=\"dxa\"/>")]);
        var page = ComposeOne(Table("<w:tblLayout w:type=\"fixed\"/>", [3000], [$"<w:tc><w:tcPr/>{nested}{P("")}</w:tc>"]));

        Assert.Contains("AlphaBetaGamma", Texts(page));
        Assert.All(Runs(page), r => Assert.True(r.Positions[^1].X < Margin + 150, $"\"{r.Text}\" runs past the outer cell."));
    }

    // ───────────────────────── Indent, Word 2010's way ─────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(720)]
    public void In_Word_2010s_compatibility_mode_the_indent_is_measured_to_the_first_cells_text(int indent)
    {
        var table = Table($"<w:tblInd w:w=\"{indent}\" w:type=\"dxa\"/>", [2000, 2000], [Cell("A"), Cell("B")]);
        const string word2010 = "<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"14\"/></w:compat>";

        // Word 2013 and later: the table's edge at the indent, the text a cell margin in.
        Assert.Equal(Margin + indent / 20f + CellMargin, Left(ComposeOne(table), "A"), 0.5f);
        // Word 2010 and older, and a document whose settings don't say (Word opens it as a Word 2007 one): the text at
        // the indent, the table's edge a cell margin out, in the margin at an indent of 0.
        Assert.Equal(Margin + indent / 20f, Left(ComposeOne(table, word2010), "A"), 0.5f);
        Assert.Equal(Margin + indent / 20f, Left(ComposeOne(table, settings: null), "A"), 0.5f);
    }

    [Fact]
    public void A_centred_table_is_not_moved_by_the_compatibility_mode()
    {
        var table = Table("<w:jc w:val=\"center\"/>", [2000, 2000], [Cell("A"), Cell("B")]);

        Assert.Equal(Left(ComposeOne(table), "A"), Left(ComposeOne(table, settings: null), "A"), 0.1f);
    }

    // ───────────────────────── Wider than the body ─────────────────────────

    [Fact]
    public void A_fixed_table_wider_than_the_body_keeps_its_widths_and_runs_into_the_margin()
    {
        // 600 pt of columns in a 451 pt text column.
        var page = ComposeOne(Table("<w:tblLayout w:type=\"fixed\"/>", [6000, 6000], [Cell("A"), Cell("B")]));

        Assert.Equal(Margin + CellMargin, Left(page, "A"), 0.5f);
        Assert.Equal(Margin + 300 + CellMargin, Left(page, "B"), 0.5f);
    }

    [Fact]
    public void A_centred_fixed_table_wider_than_the_body_runs_into_both_margins()
    {
        var page = ComposeOne(Table("<w:tblLayout w:type=\"fixed\"/><w:jc w:val=\"center\"/>", [6000, 6000], [Cell("A"), Cell("B")]));

        var edge = Margin + (TextWidth - 600) / 2;
        Assert.True(edge < Margin - 70, "The table starts in the left margin.");
        Assert.Equal(edge + CellMargin, Left(page, "A"), 0.5f);
        Assert.Equal(edge + 300 + CellMargin, Left(page, "B"), 0.5f);
    }

    [Fact]
    public void An_autofit_table_wider_than_the_body_still_fits_it()
    {
        var page = ComposeOne(Table("", [6000, 6000], [Cell("A"), Cell("B")]));

        Assert.True(Left(page, "B") < Margin + TextWidth / 2 + CellMargin + 1, "The autofit table was narrowed to the text column.");
    }

    // ───────────────────────── Cell spacing, empty grid columns ─────────────────────────

    [Fact]
    public void Cells_spaced_apart_sit_in_from_their_columns_and_rows()
    {
        // 100 twips (5 pt) around each cell: 5 pt from the table's edge, 10 pt between two cells.
        var spaced = ComposeOne(Table("<w:tblCellSpacing w:w=\"100\" w:type=\"dxa\"/>", [2000, 2000], [Cell("A"), Cell("B")], [Cell("C"), Cell("D")]));
        var touching = ComposeOne(Table("", [2000, 2000], [Cell("A"), Cell("B")], [Cell("C"), Cell("D")]));

        Assert.Equal(Margin + 5 + CellMargin, Left(spaced, "A"), 0.5f);
        Assert.Equal(Margin + 100 + 5 + CellMargin, Left(spaced, "B"), 0.5f);
        // Each row is 10 pt higher: 5 pt above and below its cells.
        Assert.Equal(Top(touching, "A") + 5, Top(spaced, "A"), 0.5f);
        Assert.Equal(Top(touching, "C") - Top(touching, "A") + 10, Top(spaced, "C") - Top(spaced, "A"), 0.5f);
    }

    [Fact]
    public void The_reader_keeps_the_cell_spacing_and_the_tables_border_around_the_cells()
    {
        var table = ReadTable(TableRows("<w:tblStyle w:val=\"TableGrid\"/>", [2000],
            $"<w:tr><w:trPr><w:tblCellSpacing w:w=\"40\" w:type=\"dxa\"/></w:trPr>{Cell("A")}</w:tr>"));

        Assert.Equal(HwpxUnits.FromTwips(40), table.CellSpacing);
        var frame = Assert.IsType<HBorders>(table.Frame);
        Assert.Equal(HBorderStyle.Solid, frame.Left.Style);
        Assert.Equal(HBorderStyle.Solid, frame.Bottom.Style);
    }

    [Fact]
    public void Grid_columns_a_row_leaves_empty_take_the_width_it_gives_them()
    {
        // No grid widths: the first column is 50 pt only because the first row says the column it skips is (w:wBefore).
        var before = ComposeOne(TableRows("", [0, 0],
            $"<w:tr><w:trPr><w:gridBefore w:val=\"1\"/><w:wBefore w:w=\"1000\" w:type=\"dxa\"/></w:trPr>{Cell("B", "<w:tcW w:w=\"2000\" w:type=\"dxa\"/>")}</w:tr>",
            $"<w:tr>{Cell("C")}{Cell("D")}</w:tr>"));
        Assert.Equal(Margin + 50 + CellMargin, Left(before, "B"), 0.5f);
        Assert.Equal(Margin + 50 + CellMargin, Left(before, "D"), 0.5f);

        // After the last cell (w:gridAfter, w:wAfter): a right-aligned table 50 + 100 pt wide.
        var after = ComposeOne(TableRows("<w:jc w:val=\"right\"/>", [0, 0],
            $"<w:tr><w:trPr><w:gridAfter w:val=\"1\"/><w:wAfter w:w=\"2000\" w:type=\"dxa\"/></w:trPr>{Cell("A", "<w:tcW w:w=\"1000\" w:type=\"dxa\"/>")}</w:tr>",
            $"<w:tr>{Cell("C")}{Cell("D")}</w:tr>"));
        Assert.Equal(Margin + TextWidth - 150 + CellMargin, Left(after, "C"), 0.5f);
        Assert.Equal(Margin + TextWidth - 100 + CellMargin, Left(after, "D"), 0.5f);
    }

    // ───────────────────────── RTF ─────────────────────────

    [Fact]
    public void An_rtf_table_with_trautofit_widens_a_column_to_its_longest_word()
    {
        // A 30 pt first column (\cellx600): the word fits it only when the row fits its columns to their content.
        string Rtf(string autofit) =>
            $@"{{\rtf1\ansi{{\fonttbl{{\f0 {RepositoryFonts.Latin};}}}}\f0\fs20\trowd{autofit}\cellx600\cellx3600\intbl {LongWord}\cell Next\cell\row\pard\par}}";
        HDocument Read(string rtf) =>
            RtfReader.Read(System.Text.Encoding.Latin1.GetBytes(rtf), Path.Combine(_folder, Guid.NewGuid().ToString("N")), Filee.Engines.Documents.DocumentLimits.Default);

        var autofit = Read(Rtf(@"\trautofit1"));
        Assert.False(Assert.IsType<HTableSizing>(autofit.Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single().Sizing).Fixed);
        Assert.Single(Runs(ComposeOne(autofit)), r => r.Text.Contains(LongWord, StringComparison.Ordinal));

        var kept = Read(Rtf(""));
        Assert.Null(kept.Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single().Sizing);
        Assert.DoesNotContain(Runs(ComposeOne(kept)), r => r.Text.Contains(LongWord, StringComparison.Ordinal));
    }

    // ───────────────────────── Columns of their own widths ─────────────────────────

    [Fact]
    public void A_table_going_on_in_a_narrower_column_is_laid_out_for_it()
    {
        // Columns of 300 pt and 126.3 pt, 25 pt apart (they add up to the text width). The table fills its column (100%);
        // 70 rows don't fit the first, so it goes on in the second, as wide as that one.
        var rows = Enumerable.Range(1, 70).Select(n => $"<w:tr>{Cell($"R{n}")}{Cell($"S{n}")}</w:tr>").ToArray();
        var body = TableRows("<w:tblW w:w=\"5000\" w:type=\"pct\"/>", [3000, 3000], rows) + P("");
        var path = new DocxBuilder
        {
            Settings = DocxBuilder.Word2013Settings,
            SectionProperties = DefaultPage + "<w:cols w:num=\"2\" w:equalWidth=\"0\"><w:col w:w=\"6000\" w:space=\"500\"/><w:col w:w=\"2526\"/></w:cols>",
        }.Paragraph(body).Save(Path.Combine(_folder, "columns.docx"));
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var page = new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(DocxReader.Read(path, Path.Combine(_folder, "media")), TestContext.Current.CancellationToken).Pages[0];

        const float second = Margin + 325;
        var narrow = Runs(page).Where(r => r.Text.StartsWith('S') && r.Positions[0].X > second).ToList();
        Assert.NotEmpty(narrow);
        // In the first column the second cell starts half of its 300 pt in; in the second, half of its 126.3 pt.
        Assert.Equal(Margin + 150 + CellMargin, Left(page, "S1"), 0.5f);
        Assert.All(narrow, r => Assert.Equal(second + 126.3f / 2 + CellMargin, r.Positions[0].X, 1f));
        Assert.All(Runs(page), r => Assert.True(r.Positions[^1].X < Margin + TextWidth, $"\"{r.Text}\" runs past the second column."));
    }

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void The_reader_keeps_the_layout_the_preferred_widths_and_Words_cell_margins()
    {
        var path = new DocxBuilder().Paragraph(
            Table("<w:tblLayout w:type=\"fixed\"/><w:tblW w:w=\"2500\" w:type=\"pct\"/>", [1440, 0],
                [Cell("A", "<w:tcW w:w=\"1440\" w:type=\"dxa\"/>"), Cell("B", "<w:tcW w:w=\"25%\" w:type=\"pct\"/>")],
                [Cell("C", "<w:tcW w:w=\"0\" w:type=\"auto\"/>"), Cell("D", "<w:tcW w:type=\"nil\"/>")]))
            .Save(Path.Combine(_folder, "read.docx"));
        var table = DocxReader.Read(path, Path.Combine(_folder, "media")).Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();

        var sizing = Assert.IsType<HTableSizing>(table.Sizing);
        Assert.True(sizing.Fixed);
        Assert.Equal(new HPreferredWidth(HWidthType.Percent, 0.5), sizing.Width);
        Assert.Equal(new[] { HwpxUnits.FromTwips(1440), 0 }, sizing.Grid);
        Assert.Equal(new HPreferredWidth(HWidthType.Absolute, HwpxUnits.FromTwips(1440)), table.Rows[0].Cells[0].Width);
        Assert.Equal(new HPreferredWidth(HWidthType.Percent, 0.25), table.Rows[0].Cells[1].Width);
        Assert.Equal(HPreferredWidth.Auto, table.Rows[1].Cells[0].Width);
        Assert.Null(table.Rows[1].Cells[1].Width);
        Assert.Equal(new HInsets(HwpxUnits.FromTwips(108), HwpxUnits.FromTwips(108), 0, 0), table.CellMargin);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string Run(string text) => R(text, $"<w:rFonts w:ascii=\"{RepositoryFonts.Latin}\" w:hAnsi=\"{RepositoryFonts.Latin}\"/><w:sz w:val=\"20\"/>");

    private static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(text.Length == 0 ? "" : Run(text))}</w:tc>";

    /// <summary>A table with the given table properties, grid (twips) and rows of cells, then an empty paragraph.</summary>
    private static string Table(string properties, int[] grid, params string[][] rows) =>
        $"<w:tbl><w:tblPr>{properties}</w:tblPr><w:tblGrid>{string.Concat(grid.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>" +
        string.Concat(rows.Select(r => $"<w:tr>{string.Concat(r)}</w:tr>")) + "</w:tbl>" + P("");

    /// <summary>The one page of a document with <paramref name="body"/>, saved by Word 2013 or later unless <paramref name="settings"/> say otherwise.</summary>
    private PageContent ComposeOne(string body, string? settings = DocxBuilder.Word2013Settings)
    {
        var path = new DocxBuilder { Settings = settings }.Paragraph(body).Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
        return ComposeOne(DocxReader.Read(path, Path.Combine(_folder, "media")));
    }

    private PageContent ComposeOne(HDocument document)
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages);
    }

    private HTable ReadTable(string body)
    {
        var path = new DocxBuilder { Settings = DocxBuilder.Word2013Settings }.Paragraph(body).Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
        return DocxReader.Read(path, Path.Combine(_folder, "media")).Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();
    }

    /// <summary>How far down the page the first run that starts with <paramref name="text"/> is (its baseline).</summary>
    private static float Top(PageContent page, string text) => Runs(page).First(r => r.Text.StartsWith(text, StringComparison.Ordinal)).Positions[0].Y;

    /// <summary>A table of the given rows (w:tr elements) after the table properties and grid, then an empty paragraph.</summary>
    private static string TableRows(string properties, int[] grid, params string[] rows) =>
        $"<w:tbl><w:tblPr>{properties}</w:tblPr><w:tblGrid>{string.Concat(grid.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>" +
        string.Concat(rows) + "</w:tbl>" + P("");

    private static List<GlyphRun> Runs(PageContent page) => [.. page.Items.OfType<GlyphItem>().Select(i => i.Run)];

    /// <summary>Where the first run that starts with <paramref name="text"/> starts.</summary>
    private static float Left(PageContent page, string text) => Runs(page).First(r => r.Text.StartsWith(text, StringComparison.Ordinal)).Positions[0].X;

    /// <summary>The text of each line of a page, top to bottom, without spaces.</summary>
    private static List<string> Texts(PageContent page) =>
        [.. Runs(page).GroupBy(r => MathF.Round(r.Positions[0].Y, 1)).OrderBy(g => g.Key)
            .Select(g => new string([.. string.Concat(g.Select(r => r.Text)).Where(c => !char.IsWhiteSpace(c))]))];
}
