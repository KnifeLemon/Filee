// Word's headers and footers in the layout engine (Typesetting/PageComposer.cs, issues #1152, #416, #556): DOCX built
// in code, read by DocxReader and composed as the renderer composes Word documents. The first page's own header and
// footer ("different first page"), odd and even pages, a header or footer taller than its area moving the body, the
// footer growing up from its distance to the paper's bottom edge, and the header of a continuous section break taking
// effect on the next page. Laid out with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts); without them the
// tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordHeaderLayoutTests : IDisposable
{
    /// <summary>A4 height in points.</summary>
    private const float PaperHeight = 16838 / 20f;

    private const string TitlePage = "<w:titlePg/>";

    private const string Continuous = "<w:type w:val=\"continuous\"/>";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-word-headers").FullName;

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

    // ───────────────────────── First page, odd and even pages ─────────────────────────

    [Fact]
    public void The_first_page_has_its_own_header_and_footer_and_the_others_the_default_ones()
    {
        var docx = new DocxBuilder();
        var (firstHeader, header) = (docx.Header(P(Run("CoverHeader"))), docx.Header(P(Run("DefaultHeader"))));
        var (firstFooter, footer) = (docx.Footer(P(Run("CoverFooter"))), docx.Footer(P(Run("DefaultFooter"))));
        docx.Paragraph(P(Run("One")) + P(Run("Two"), PageBreak));
        docx.SectionProperties = Reference("header", "default", header) + Reference("header", "first", firstHeader) +
                                 Reference("footer", "default", footer) + Reference("footer", "first", firstFooter) + DefaultPage + TitlePage;

        var pages = Compose(docx);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["CoverHeader", "One", "CoverFooter"], Texts(pages[0]));
        Assert.Equal(["DefaultHeader", "Two", "DefaultFooter"], Texts(pages[1]));
    }

    [Fact]
    public void A_blank_first_page_header_leaves_the_first_page_without_one()
    {
        var docx = new DocxBuilder();
        var (firstHeader, header) = (docx.Header(P("")), docx.Header(P(Run("DefaultHeader"))));
        docx.Paragraph(P(Run("One")) + P(Run("Two"), PageBreak));
        docx.SectionProperties = Reference("header", "default", header) + Reference("header", "first", firstHeader) + DefaultPage + TitlePage;

        var pages = Compose(docx);

        Assert.Equal(["One"], Texts(pages[0]));
        Assert.Equal(["DefaultHeader", "Two"], Texts(pages[1]));
    }

    [Fact]
    public void Odd_and_even_pages_take_their_headers_when_the_settings_say_so()
    {
        static DocxBuilder Build(bool evenAndOdd)
        {
            var docx = new DocxBuilder { Settings = evenAndOdd ? "<w:evenAndOddHeaders/>" : null };
            var (odd, even) = (docx.Header(P(Run("OddHeader"))), docx.Header(P(Run("EvenHeader"))));
            docx.Paragraph(P(Run("One")) + P(Run("Two"), PageBreak) + P(Run("Three"), PageBreak));
            docx.SectionProperties = Reference("header", "default", odd) + Reference("header", "even", even) + DefaultPage;
            return docx;
        }

        var pages = Compose(Build(evenAndOdd: true));
        var plain = Compose(Build(evenAndOdd: false));

        Assert.Equal(["OddHeader", "One"], Texts(pages[0]));
        Assert.Equal(["EvenHeader", "Two"], Texts(pages[1]));
        Assert.Equal(["OddHeader", "Three"], Texts(pages[2]));
        // Without the setting Word shows the default header on every page.
        Assert.All(plain, page => Assert.Equal("OddHeader", Texts(page)[0]));
    }

    // ───────────────────────── Tall headers and footers ─────────────────────────

    [Fact]
    public void A_header_taller_than_the_room_above_the_body_moves_the_body_down()
    {
        // A 4 cm table in the header, 0.5 inch from the top, under a 2.5 cm top margin.
        var pages = Compose(TallHeader(top: 1418));

        var lastRow = Baseline(pages[0], "Row3");
        var body = Baseline(pages[0], "Contents");
        Assert.True(body > lastRow + 10, $"the body ({body}) starts below the header's last line ({lastRow})");
        // Below the 4 cm table (from 36 pt) and the header's last, empty paragraph.
        Assert.InRange(body, 36 + 4 * 28.35f + 10, 36 + 4 * 28.35f + 40);
        // On every page.
        Assert.Equal(body, Baseline(pages[1], "Second"), 0.01f);
    }

    [Fact]
    public void A_negative_top_margin_keeps_the_body_where_the_margin_puts_it()
    {
        var pages = Compose(TallHeader(top: -1418));

        // Word draws the header over the body then: the body's first line is at the 2.5 cm margin, inside the table.
        Assert.InRange(Baseline(pages[0], "Contents"), 70.9f, 70.9f + 15);
    }

    [Fact]
    public void A_short_footer_ends_at_its_distance_from_the_bottom_and_a_tall_one_moves_the_body_up()
    {
        var docx = new DocxBuilder();
        var footer = docx.Footer(Table("Foot", 4));
        docx.Paragraph(string.Concat(Enumerable.Range(0, 80).Select(i => P(Run($"Line{i}")))));
        docx.SectionProperties = Reference("footer", "default", footer) + Page(top: 1418, bottom: 1418);
        var shortDocx = new DocxBuilder();
        var shortFooter = shortDocx.Footer(P(Run("Short")));
        shortDocx.Paragraph(P(Run("Contents")));
        shortDocx.SectionProperties = Reference("footer", "default", shortFooter) + Page(top: 1418, bottom: 1418);

        var pages = Compose(docx);
        var single = Assert.Single(Compose(shortDocx));

        // The one-line footer ends 0.5 inch (36 pt) above the paper's bottom edge: its baseline a little above that.
        Assert.InRange(Baseline(single, "Short"), PaperHeight - 36 - 8, PaperHeight - 36 - 1);
        // The 4 cm table ends there too; the body's last line on the first page is above the table's first row.
        var firstRow = Baseline(pages[0], "Foot0");
        Assert.InRange(firstRow, PaperHeight - 36 - 4 * 28.35f - 20, PaperHeight - 36 - 3 * 28.35f);
        var lastBody = Runs(pages[0]).Where(r => r.Text.StartsWith("Line", StringComparison.Ordinal)).Max(r => r.Positions[0].Y);
        Assert.True(lastBody < firstRow - 5, $"the body ({lastBody}) ends above the footer ({firstRow})");
        Assert.True(pages.Count >= 2);
    }

    // ───────────────────────── Continuous section breaks ─────────────────────────

    [Fact]
    public void The_header_of_a_continuous_section_takes_effect_on_the_next_page()
    {
        var docx = new DocxBuilder();
        var (one, two) = (docx.Header(P(Run("HeaderOne"))), docx.Header(P(Run("HeaderTwo"))));
        docx.Paragraph(P(Run("Contents")) + SectionEnd(Reference("header", "default", one)) + P(Run("After")) + P(Run("Second"), PageBreak));
        docx.SectionProperties = Reference("header", "default", two) + Continuous + DefaultPage;

        var pages = Compose(docx);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["HeaderOne", "Contents", "After"], Texts(pages[0]));
        Assert.Equal(["HeaderTwo", "Second"], Texts(pages[1]));
    }

    [Fact]
    public void A_continuous_section_that_starts_a_page_shows_its_header_there()
    {
        var docx = new DocxBuilder();
        var (one, two) = (docx.Header(P(Run("HeaderOne"))), docx.Header(P(Run("HeaderTwo"))));
        docx.Paragraph(P(Run("Contents")) + SectionEnd(Reference("header", "default", one)) + P(Run("After"), "<w:pageBreakBefore/>") + P(Run("Third"), PageBreak));
        docx.SectionProperties = Reference("header", "default", two) + Continuous + DefaultPage;

        var pages = Compose(docx);

        Assert.Equal(3, pages.Count);
        Assert.Equal("HeaderOne", Texts(pages[0])[0]);
        Assert.Equal(["HeaderTwo", "After"], Texts(pages[1]));
        Assert.Equal(["HeaderTwo", "Third"], Texts(pages[2]));
    }

    [Fact]
    public void A_header_starting_on_an_empty_page_moves_the_body_for_itself()
    {
        // The model of a continuous section starting at the top of a page: its header applies there, and its height
        // moves the body there.
        SkipWithoutFonts();
        var first = Plain("First");
        var section = new HSection { Page = new HPage(20000, 30000, 2000, 2000, 1000, 1000, 1000, 1000, 0), HeaderMovesBody = true };
        section.Blocks.Add(first);
        section.Headers.Add(Header(Plain("Old")));
        var tall = Header(Plain("New1"), Plain("New2"), Plain("New3"));
        tall.StartsAt = first;
        section.Headers.Add(tall);
        var document = new HDocument();
        document.Sections.Add(section);

        var page = Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken).Pages);

        Assert.Equal(["New1", "New2", "New3", "First"], Texts(page));
        Assert.True(Baseline(page, "First") > Baseline(page, "New3") + 10);
    }

    // ───────────────────────── Reader ─────────────────────────

    [Fact]
    public void The_reader_keeps_a_first_page_footer_and_the_margins_that_say_whether_the_body_moves()
    {
        var docx = new DocxBuilder();
        var (first, other) = (docx.Footer(P(Run("CoverFooter"))), docx.Footer(P(Run("DefaultFooter"))));
        docx.Paragraph(P(Run("Body")) + SectionEnd(Reference("footer", "first", first) + Reference("footer", "default", other) + TitlePage));
        docx.SectionProperties = Page(top: -1418, bottom: -1418);

        var document = DocxReader.Read(docx.Save(Path.Combine(_folder, "source.docx")), Path.Combine(_folder, "media"));

        Assert.Equal(2, document.Sections.Count);
        var (one, two) = (document.Sections[0], document.Sections[1]);
        Assert.False(one.HideFirstFooter);
        Assert.Equal("CoverFooter", Assert.IsType<HText>(Assert.IsType<HParagraph>(Assert.Single(one.FirstPageFooter!)).Inlines.Single()).Text);
        Assert.True(one.HeaderMovesBody && one.FooterMovesBody && one.FooterGrowsUp);
        Assert.False(two.HeaderMovesBody || two.FooterMovesBody);
        // The next section has no "different first page" of its own.
        Assert.Null(two.FirstPageFooter);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private const string PageBreak = "<w:pageBreakBefore/>";

    private static string Run(string text) => R(text, $"<w:rFonts w:ascii=\"{RepositoryFonts.Latin}\" w:hAnsi=\"{RepositoryFonts.Latin}\"/><w:sz w:val=\"20\"/>");

    private static string Reference(string kind, string type, string id) => $"<w:{kind}Reference w:type=\"{type}\" r:id=\"{id}\"/>";

    private static string SectionEnd(string properties) => P("", $"<w:sectPr>{properties}{DefaultPage}</w:sectPr>");

    private static string Page(int top, int bottom = 1440) =>
        $"<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"{top}\" w:right=\"1440\" w:bottom=\"{bottom}\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>";

    /// <summary>A one-column table of <paramref name="rows"/> rows exactly 1 cm high, each with "<paramref name="label"/>N".</summary>
    private static string Table(string label, int rows) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"9026\" w:type=\"dxa\"/><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"9026\"/></w:tblGrid>" +
        string.Concat(Enumerable.Range(0, rows).Select(i =>
            $"<w:tr><w:trPr><w:trHeight w:val=\"567\" w:hRule=\"exact\"/></w:trPr><w:tc><w:tcPr><w:tcW w:w=\"9026\" w:type=\"dxa\"/></w:tcPr>{P(Run($"{label}{i}"))}</w:tc></w:tr>")) +
        "</w:tbl>" + P("");

    private static DocxBuilder TallHeader(int top)
    {
        var docx = new DocxBuilder();
        var header = docx.Header(Table("Row", 4));
        docx.Paragraph(P(Run("Contents")) + P(Run("Second"), PageBreak));
        docx.SectionProperties = Reference("header", "default", header) + Page(top);
        return docx;
    }

    private List<PageContent> Compose(DocxBuilder docx)
    {
        SkipWithoutFonts();
        var path = docx.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
        var document = DocxReader.Read(path, Path.Combine(_folder, "media"));
        return new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages;
    }

    private void SkipWithoutFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    private static HParagraph Plain(string text)
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        paragraph.Inlines.Add(new HText(text, Format()));
        return paragraph;
    }

    private static HHeaderFooter Header(params HParagraph[] paragraphs)
    {
        var header = new HHeaderFooter(HPageType.Both);
        header.Blocks.AddRange(paragraphs);
        return header;
    }

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    /// <summary>The baseline of the first run of a page that starts with <paramref name="text"/>.</summary>
    private static float Baseline(PageContent page, string text) =>
        Runs(page).First(r => r.Text.StartsWith(text, StringComparison.Ordinal)).Positions[0].Y;

    /// <summary>The text of each line of a page, top to bottom, without spaces.</summary>
    private static List<string> Texts(PageContent page) =>
        [.. Runs(page).GroupBy(r => MathF.Round(r.Positions[0].Y, 1)).OrderBy(g => g.Key)
            .Select(g => new string([.. string.Concat(g.Select(r => r.Text)).Where(c => !char.IsWhiteSpace(c))]))];
}
