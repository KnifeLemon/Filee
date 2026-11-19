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

    private PageContent ComposeOne(string body)
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var path = new DocxBuilder().Paragraph(body).Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
        var document = DocxReader.Read(path, Path.Combine(_folder, "media"));
        return Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages);
    }

    private static List<GlyphRun> Runs(PageContent page) => [.. page.Items.OfType<GlyphItem>().Select(i => i.Run)];

    /// <summary>Where the first run that starts with <paramref name="text"/> starts.</summary>
    private static float Left(PageContent page, string text) => Runs(page).First(r => r.Text.StartsWith(text, StringComparison.Ordinal)).Positions[0].X;

    /// <summary>The text of each line of a page, top to bottom, without spaces.</summary>
    private static List<string> Texts(PageContent page) =>
        [.. Runs(page).GroupBy(r => MathF.Round(r.Positions[0].Y, 1)).OrderBy(g => g.Key)
            .Select(g => new string([.. string.Concat(g.Select(r => r.Text)).Where(c => !char.IsWhiteSpace(c))]))];
}
