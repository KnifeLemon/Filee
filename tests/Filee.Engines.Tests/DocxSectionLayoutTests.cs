// Word section properties the renderer lays out: columns of their own widths (w:cols equalWidth="0", issue #1584) and
// page borders (w:pgBorders, issue #556). Documents are built here; the drawing checks use the Noto fonts of
// build/fetch-fonts.ps1 (RepositoryFonts) and skip without them.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class DocxSectionLayoutTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>A 200 × 200 pt page with 20 pt margins all round and no header or footer distance.</summary>
    private const string SmallPage = "<w:pgSz w:w=\"4000\" w:h=\"4000\"/><w:pgMar w:top=\"400\" w:right=\"400\" w:bottom=\"400\" w:left=\"400\" w:header=\"400\" w:footer=\"400\" w:gutter=\"0\"/>";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private HDocument Read(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
    }

    [Fact]
    public void Columns_of_their_own_widths_are_read_with_the_space_after_each()
    {
        // As in #1584's invoice: three columns of 8422, 861 and 2238 twips.
        var document = Read(new DocxBuilder
        {
            SectionProperties = DefaultPage + "<w:cols w:equalWidth=\"0\" w:num=\"3\" w:space=\"100\"><w:col w:w=\"8422\" w:space=\"100\"/>" +
                                "<w:col w:w=\"861\" w:space=\"16\"/><w:col w:w=\"2238\"/></w:cols>",
        }.Paragraph(P(R("text"))));

        var columns = Assert.Single(document.Sections).Columns;
        Assert.Equal(3, columns.Count);
        Assert.Equal([new HColumnSize(42110, 500), new HColumnSize(4305, 80), new HColumnSize(11190, 0)], columns.Sizes);
        Assert.Equal(500, columns.Gap);
    }

    [Fact]
    public void Columns_of_their_own_widths_are_written_to_word_and_hangul()
    {
        var document = Read(new DocxBuilder
        {
            SectionProperties = DefaultPage + "<w:cols w:equalWidth=\"0\" w:num=\"2\" w:sep=\"1\"><w:col w:w=\"6000\" w:space=\"500\"/><w:col w:w=\"2526\"/></w:cols>",
        }.Paragraph(P(R("text"))));
        var columns = Assert.Single(document.Sections).Columns;
        Assert.NotNull(columns.Sizes);
        var dir = fx.NewFolder();

        var docx = Path.Combine(dir, "again.docx");
        DocxWriter.Write(document, docx);
        DocxAssert.ValidPackage(docx);
        Assert.Equal(columns, Assert.Single(DocxReader.Read(docx, Path.Combine(dir, "media-docx")).Sections).Columns);

        var hwpx = Path.Combine(dir, "again.hwpx");
        HwpxWriter.Write(document, hwpx, cancellationToken: TestContext.Current.CancellationToken);
        HwpxAssert.ValidPackage(hwpx);
        Assert.Equal(columns with { RightToLeft = false }, Assert.Single(HwpxReader.Read(hwpx, Path.Combine(dir, "media-hwpx")).Sections).Columns);
    }

    [Fact]
    public void Columns_listed_with_one_width_or_left_equal_stay_equal()
    {
        var same = Read(new DocxBuilder
        {
            SectionProperties = DefaultPage + "<w:cols w:equalWidth=\"0\" w:num=\"2\"><w:col w:w=\"4000\" w:space=\"720\"/><w:col w:w=\"4000\"/></w:cols>",
        }.Paragraph(P(R("text"))));
        var equal = Read(new DocxBuilder
        {
            SectionProperties = DefaultPage + "<w:cols w:num=\"2\" w:space=\"720\"><w:col w:w=\"1000\" w:space=\"100\"/><w:col w:w=\"7000\"/></w:cols>",
        }.Paragraph(P(R("text"))));

        Assert.Equal(new HColumns(2, HwpxUnits.FromTwips(720), false), Assert.Single(same.Sections).Columns);
        Assert.Equal(new HColumns(2, HwpxUnits.FromTwips(720), false), Assert.Single(equal.Sections).Columns);
    }

    [Fact]
    public void A_title_in_the_wide_column_of_a_word_section_stays_on_one_line()
    {
        // 160 pt of text: a 130 pt column, a 10 pt gap and a 20 pt one. Split equally, the title would wrap at 75 pt.
        var docx = new DocxBuilder
        {
            SectionProperties = SmallPage + "<w:cols w:equalWidth=\"0\" w:num=\"2\" w:space=\"200\"><w:col w:w=\"2600\" w:space=\"200\"/><w:col w:w=\"400\"/></w:cols>",
        };
        docx.Paragraph(P(R("A wide title over it", $"<w:rFonts w:ascii=\"{RepositoryFonts.Latin}\" w:hAnsi=\"{RepositoryFonts.Latin}\"/><w:sz w:val=\"20\"/>")))
            .Paragraph(P("<w:r><w:br w:type=\"column\"/></w:r>" + R("No")));

        var page = Compose(Read(docx)).Pages[0];

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        Assert.Single(runs.Where(r => r.Positions[0].X < 150).Select(r => MathF.Round(r.Positions[0].Y)).Distinct());
        Assert.Equal(160f, runs.Single(r => r.Text.Contains("No")).Positions[0].X, 0.5f);
    }

    [Fact]
    public void Page_borders_are_read_with_their_lines_offsets_and_pages()
    {
        var document = Read(new DocxBuilder
        {
            SectionProperties = DefaultPage + "<w:pgBorders w:offsetFrom=\"page\" w:display=\"notFirstPage\">" +
                                "<w:top w:val=\"single\" w:sz=\"12\" w:space=\"24\" w:color=\"auto\"/>" +
                                "<w:left w:val=\"thinThickSmallGap\" w:sz=\"8\" w:space=\"20\" w:color=\"FF0000\"/>" +
                                "<w:bottom w:val=\"apples\" w:sz=\"20\" w:space=\"24\"/>" +
                                "<w:right w:val=\"dashed\" w:sz=\"4\" w:space=\"40\"/></w:pgBorders>",
        }.Paragraph(P(R("text"))));

        var section = Assert.Single(document.Sections);
        var border = Assert.Single(section.PageBorders);
        static double Mm(double points) => points * 25.4 / 72;
        // Lines of w:sz eighths of a point; a two-line style is drawn double (two lines and the space between them),
        // a picture border (apples) a single line.
        Assert.Equal(new HBorders(
            Left: new HBorder(HBorderStyle.Double, Mm(3), "#FF0000"),
            Right: new HBorder(HBorderStyle.Dash, Mm(0.5), "#000000"),
            Top: new HBorder(HBorderStyle.Solid, Mm(1.5), "#000000"),
            Bottom: new HBorder(HBorderStyle.Solid, Mm(1.5), "#000000")), border.Borders);
        // From the paper's edges, w:space points (at most 31) to the middle of the line.
        Assert.False(border.AroundBody);
        Assert.Equal(new HInsets(2150, 3125, 2475, 2475), border.Offsets);
        Assert.Equal(HFirstPageRule.HideFirst, section.PageBorderShows);
    }

    [Fact]
    public void A_page_border_is_drawn_from_the_paper_or_around_the_text()
    {
        const string Box = "<w:top w:val=\"single\" w:sz=\"16\" w:space=\"10\"/><w:left w:val=\"single\" w:sz=\"16\" w:space=\"10\"/>" +
                           "<w:bottom w:val=\"single\" w:sz=\"16\" w:space=\"10\"/><w:right w:val=\"single\" w:sz=\"16\" w:space=\"10\"/>";
        SKBitmap Render(string offsetFrom) => Compose(Read(new DocxBuilder
        {
            SectionProperties = SmallPage + $"<w:pgBorders w:offsetFrom=\"{offsetFrom}\">{Box}</w:pgBorders>",
        }.Paragraph(P(R("text"))))).Pages[0].Render(72);

        // From the page: 10 pt in plus half the 2 pt line, so the line covers 10..12 pt from the edges.
        using (var page = Render("page"))
        {
            Assert.True(page.GetPixel(11, 100).Red < 128);
            Assert.True(page.GetPixel(100, 188).Red < 128);
            Assert.Equal(SKColors.White, page.GetPixel(3, 100));
        }
        // From the text (20 pt margins): 10 pt out from it, at 8..10 pt from the edges, around the header and footer areas too.
        using (var text = Render("text"))
        {
            Assert.True(text.GetPixel(9, 100).Red < 128);
            Assert.True(text.GetPixel(100, 9).Red < 128);
            Assert.Equal(SKColors.White, text.GetPixel(11, 100));
        }
    }

    private ComposedDocument Compose(HDocument document)
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken);
    }
}
