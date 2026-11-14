// Paragraph borders and backgrounds in the layout engine (Typesetting/ParagraphBorders.cs): 한글's boxes from the model,
// Word's from DOCX built in code, a block quote's bar and background and a code box from Markdown, in the body, across
// a page break and in a header. The pages are drawn at 4 pixels per point and the lines and fills looked for along a
// row or a column of pixels. Laid out with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts); without them the
// tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class ParagraphBorderTests : IDisposable
{
    /// <summary>200 × 300 pt paper, 20 pt margins, no header or footer area: a body of 160 × 260 pt from (20, 20).</summary>
    private static readonly HPage Paper = new(20000, 30000, 2000, 2000, 2000, 2000, 0, 0, 0);

    /// <summary>Pixels per point the pages are drawn at.</summary>
    private const int Scale = 4;

    /// <summary>A 0.5 mm black line: 1.42 pt.</summary>
    private static readonly HBorder Line = new(HBorderStyle.Solid, 0.5, "#000000");

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-borders").FullName;

    public void Dispose()
    {
        _shaper.Dispose();
        foreach (var bitmap in _drawn.Values)
            bitmap.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────────────────────── 한글 ─────────────────────────

    [Fact]
    public void Connected_paragraphs_with_one_border_share_one_box_and_their_text_moves_in()
    {
        var border = new HParaBorder(new HBorders(Line, Line, Line, Line), null, new HInsets(500, 500, 300, 300), Connect: true);
        var page = Single(Section(Boxed("one", border), Boxed("two", border), Plain("after")));

        // One box: a line at its top and one at its bottom, none between the paragraphs.
        var rows = DarkRows(page, 170);
        Assert.Equal(2, rows.Count);
        Assert.Equal(20f, rows[0].Top, 0.3f);
        // Inside: the top line, 3 pt, two 16 pt lines, 3 pt, the bottom line.
        Assert.Equal(20f + 1.42f + 3 + 32 + 3 + 1.42f, rows[1].Bottom, 0.5f);
        // The sides at the column's edges, down the whole box.
        Assert.Equal([20f, 180f - 1.42f], DarkColumns(page, Baseline(page, "two")).Select(c => MathF.Round(c.Left, 1)), new Tolerance(0.3f));
        // 한글 moves the text in by the line and its 5 pt offset.
        Assert.Equal(20f + 1.42f + 5, X(page, "one"), 0.3f);
        Assert.Equal(20f, X(page, "after"), 0.3f);
        // The paragraph after the box starts below it.
        Assert.True(Baseline(page, "after") > rows[1].Bottom + 8);
    }

    [Fact]
    public void Paragraphs_not_connected_draw_a_box_each()
    {
        var border = new HParaBorder(new HBorders(Line, Line, Line, Line), null, default);
        var page = Single(Section(Boxed("one", border), Boxed("two", border)));

        // The first box's bottom line and the second's top line meet: one line twice as thick between them.
        var rows = DarkRows(page, 170);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2 * 1.42f, rows[1].Bottom - rows[1].Top, 0.5f);
    }

    [Fact]
    public void A_box_split_by_a_page_break_is_left_open_at_the_break()
    {
        var border = new HParaBorder(new HBorders(Line, Line, Line, Line), null, default, Connect: true);
        // Twenty 16 pt lines: the 260 pt body holds sixteen of them.
        var pages = Compose(Section([.. Enumerable.Range(0, 20).Select(i => Boxed($"line {i}", border))])).Pages;

        Assert.Equal(2, pages.Count);
        // First page: the top line only, the sides down to the last line there.
        var first = DarkRows(pages[0], 170);
        Assert.Equal(20f, Assert.Single(first).Top, 0.3f);
        var sides = DarkRows(pages[0], 20.5f);
        Assert.Equal(20f, sides[0].Top, 0.3f);
        Assert.True(sides[^1].Bottom > 20 + 15 * 16, $"{sides[^1].Bottom}");
        // Second page: no top line, the bottom one under the last line.
        var second = DarkRows(pages[1], 170);
        Assert.Single(second);
        Assert.True(second[0].Top > Baseline(pages[1], "line 19"));
        Assert.True(DarkRows(pages[1], 20.5f)[0].Top < 20.5f);
    }

    [Fact]
    public void A_header_paragraph_with_a_bottom_border_draws_its_rule()
    {
        var section = Section(Plain("body"));
        section.Page = Paper with { Header = 3000 };
        var header = new HHeaderFooter(HPageType.Both);
        header.Blocks.Add(Boxed("head", new HParaBorder(HBorders.Empty with { Bottom = Line }, null, default)));
        section.Headers.Add(header);

        var page = Single(section);

        // The rule under the header's line, between the header area's top (20) and the body's (50).
        var rule = Assert.Single(DarkRows(page, 170));
        Assert.Equal(20f + 16, rule.Top, 0.5f);
        Assert.True(rule.Bottom < 50);
    }

    [Fact]
    public void A_background_fills_the_box_and_the_spacing_between_its_paragraphs()
    {
        var shade = new HParaBorder(HBorders.Empty, "#FFE0A0", default, Connect: true);
        HParagraph Shaded(string text) => Boxed(text, shade, new HParaFormat(Before: 0, After: 1000));
        var page = Single(Section(Plain("before"), Shaded("one"), Shaded("two"), Plain("after")));

        bool Filled(float y) => Pixel(page, 170, y) == new SKColor(0xFF, 0xE0, 0xA0);
        var (one, two, after) = (Baseline(page, "one"), Baseline(page, "two"), Baseline(page, "after"));
        Assert.True(Filled(one));
        // The 10 pt after "one" is inside the box; the 10 pt after "two" is not.
        Assert.True(Filled(two - 14));
        Assert.True(Filled(two));
        Assert.False(Filled(after - 14));
        // Glyphs over it: the background goes under the text.
        Assert.Contains(Enumerable.Range(0, 60), i => Dark(Pixel(page, X(page, "one") + i * 0.25f, one - 3)));
    }

    // ───────────────────────── Word ─────────────────────────

    [Fact]
    public void A_word_heading_rule_goes_under_its_text_and_takes_its_room()
    {
        const string Rule = "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"4\" w:color=\"000000\"/></w:pBdr>";
        var ruled = Single(Word(P(Run("Heading"), Rule + Tight), P(Run("Body"), Tight)));
        var plain = Single(Word(P(Run("Heading"), Tight), P(Run("Body"), Tight)));

        // The line (1.5 pt) 4 pt under the heading's line, across the text width.
        var rule = Assert.Single(DarkRows(ruled, 400));
        Assert.Equal(1.5f, rule.Bottom - rule.Top, 0.5f);
        Assert.True(rule.Top > Baseline(ruled, "Heading"));
        // The next paragraph moves down by the line and its distance.
        Assert.Equal(5.5f, Baseline(ruled, "Body") - Baseline(plain, "Body"), 0.1f);
        Assert.Empty(DarkRows(plain, 400));
    }

    [Fact]
    public void A_word_box_goes_outside_the_text_which_keeps_its_place()
    {
        const string Box = "<w:pBdr><w:top w:val=\"single\" w:sz=\"8\" w:space=\"1\" w:color=\"000000\"/><w:left w:val=\"single\" w:sz=\"8\" w:space=\"4\" w:color=\"000000\"/>" +
                           "<w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"1\" w:color=\"000000\"/><w:right w:val=\"single\" w:sz=\"8\" w:space=\"4\" w:color=\"000000\"/></w:pBdr>";
        var boxed = Single(Word(P(Run("One"), Box + Tight), P(Run("Two"), Box + Tight)));
        var plain = Single(Word(P(Run("One"), Tight), P(Run("Two"), Tight)));

        Assert.Equal(X(plain, "One"), X(boxed, "One"), 0.01f);
        // Word's margins are 72 pt: the left line's inner edge 4 pt left of the text, in the margin.
        var sides = DarkColumns(boxed, Baseline(boxed, "Two"));
        Assert.Equal(72f - 4 - 1, sides[0].Left, 0.3f);
        Assert.Equal(595.3f - 72 + 4 + 1, sides[^1].Right, 0.5f);
        // One box: the same borders on both paragraphs join.
        Assert.Equal(2, DarkRows(boxed, 400).Count);
    }

    [Fact]
    public void Word_draws_dotted_double_and_between_lines()
    {
        const string Box = "<w:pBdr><w:top w:val=\"dotted\" w:sz=\"16\" w:space=\"1\" w:color=\"000000\"/><w:bottom w:val=\"double\" w:sz=\"24\" w:space=\"1\" w:color=\"000000\"/>" +
                           "<w:between w:val=\"single\" w:sz=\"8\" w:space=\"1\" w:color=\"000000\"/></w:pBdr>";
        var page = Single(Word(P(Run("One"), Box + Tight), P(Run("Two"), Box + Tight)));

        // Under the dotted top line (2 pt, from the body's top at 72 pt): the between line and the double bottom's two.
        var rows = DarkRows(page, 400).Where(r => r.Top > 75).ToList();
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Top > Baseline(page, "One") && rows[0].Bottom < Baseline(page, "Two"));
        Assert.True(rows[1].Top > Baseline(page, "Two"));
        // The dotted line has gaps along it, the between line none.
        var dotted = Enumerable.Range(0, 160).Select(i => Dark(Pixel(page, 300 + i * 0.25f, 73))).ToList();
        Assert.Contains(true, dotted);
        Assert.Contains(false, dotted);
        Assert.All(Enumerable.Range(0, 160), i => Assert.True(Dark(Pixel(page, 300 + i * 0.25f, (rows[0].Top + rows[0].Bottom) / 2))));
    }

    [Fact]
    public void A_word_heading_rule_reaches_the_pdf()
    {
        const string Rule = "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"4\" w:color=\"000000\"/></w:pBdr>";
        SkipWithoutFonts();
        var document = DocxReader.Read(Docx(P(Run("Heading"), Rule + Tight)), Path.Combine(_folder, "media"));

        var pdf = FileeRenderer.ToPdf(document, TestContext.Current.CancellationToken, styles: StyleResolver.FromTemplate());

        // Drawn by PDFium at 72 dpi: a dark row across the page right of the text, between the heading and 40 pt below it.
        var picture = GreyImage.Pages(pdf, 72)[0];
        Assert.Contains(Enumerable.Range(72, 40), y => Enumerable.Range(300, 100).All(x => picture[x, y] < 128));
    }

    // ───────────────────────── Markdown ─────────────────────────

    [Fact]
    public void A_markdown_quote_draws_its_bar_and_background_and_code_its_box()
    {
        SkipWithoutFonts();
        var document = MarkdownReader.Read("> Quoted text.\n>\n> Second quoted paragraph.\n\n```\ncode line\n```\n", _folder);
        var page = new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages[0];

        var (left, quote) = (X(page, "Quoted"), Baseline(page, "Quoted"));
        // The 1 mm bar at the paragraph's left edge, the background right of it; the text 2 mm right of the bar.
        var bar = new SKColor(0xB8, 0xB8, 0xC0);
        var fill = new SKColor(0xF7, 0xF7, 0xF9);
        var edge = left - 2.83f - 5.67f;
        Assert.Equal(bar, Pixel(page, edge + 1.4f, quote));
        Assert.Equal(fill, Pixel(page, edge + 4.5f, quote - 2));
        Assert.Equal(fill, Pixel(page, 400, quote));
        // One box through both paragraphs: the bar runs on between them.
        var second = Baseline(page, "Second");
        Assert.All(Enumerable.Range(0, (int)(second - quote)), d => Assert.Equal(bar, Pixel(page, edge + 1.4f, quote + d)));
        // The code block's shaded box.
        Assert.Equal(new SKColor(0xF1, 0xF0, 0xF5), Pixel(page, 400, Baseline(page, "code") - 2));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private const string Tight = "<w:spacing w:before=\"0\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/>";

    private static string Run(string text) => R(text, $"<w:rFonts w:ascii=\"{RepositoryFonts.Latin}\" w:hAnsi=\"{RepositoryFonts.Latin}\"/><w:sz w:val=\"20\"/>");

    private string Docx(params string[] paragraphs)
    {
        var builder = new DocxBuilder();
        foreach (var paragraph in paragraphs)
            builder.Paragraph(paragraph);
        return builder.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
    }

    /// <summary>A DOCX of <paramref name="paragraphs"/> composed as the renderer composes Word documents.</summary>
    private ComposedDocument Word(params string[] paragraphs)
    {
        SkipWithoutFonts();
        var document = DocxReader.Read(Docx(paragraphs), Path.Combine(_folder, "media"));
        return new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate()).Compose(document, TestContext.Current.CancellationToken);
    }

    private void SkipWithoutFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private ComposedDocument Compose(HSection section)
    {
        SkipWithoutFonts();
        var document = new HDocument();
        document.Sections.Add(section);
        return new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken);
    }

    private PageContent Single(HSection section) => Assert.Single(Compose(section).Pages);

    private static PageContent Single(ComposedDocument document) => Assert.Single(document.Pages);

    private static HSection Section(params HBlock[] blocks)
    {
        var section = new HSection { Page = Paper };
        section.Blocks.AddRange(blocks);
        return section;
    }

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    private static HParagraph Plain(string text, HParaFormat? format = null)
    {
        var paragraph = new HParagraph { Format = format ?? new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        paragraph.Inlines.Add(new HText(text, Format()));
        return paragraph;
    }

    private static HParagraph Boxed(string text, HParaBorder border, HParaFormat? format = null)
    {
        var paragraph = Plain(text, format);
        paragraph.Format = paragraph.Format with { Layout = new HParaLayout(Border: border) };
        return paragraph;
    }

    private readonly Dictionary<PageContent, SKBitmap> _drawn = new(ReferenceEqualityComparer.Instance);

    /// <summary>The page's pixel at (<paramref name="x"/>, <paramref name="y"/>) in points.</summary>
    private SKColor Pixel(PageContent page, float x, float y)
    {
        if (!_drawn.TryGetValue(page, out var bitmap))
            _drawn[page] = bitmap = page.Render(72 * Scale);
        return bitmap.GetPixel((int)(x * Scale), (int)(y * Scale));
    }

    private static bool Dark(SKColor color) => color.Red < 128 && color.Green < 128 && color.Blue < 128;

    /// <summary>The dark stretches down the page at <paramref name="x"/>, top to bottom, in points.</summary>
    private List<(float Top, float Bottom)> DarkRows(PageContent page, float x)
    {
        var runs = new List<(float, float)>();
        int? start = null;
        var height = (int)(page.Height * Scale);
        for (var y = 0; y <= height; y++)
        {
            var dark = y < height && Dark(Pixel(page, x, (y + 0.5f) / Scale));
            if (dark && start is null)
                start = y;
            else if (!dark && start is { } from)
            {
                runs.Add((from / (float)Scale, y / (float)Scale));
                start = null;
            }
        }
        return runs;
    }

    /// <summary>The dark stretches across the page at <paramref name="y"/>, outside the text (left of it and right of 150 pt).</summary>
    private List<(float Left, float Right)> DarkColumns(PageContent page, float y)
    {
        var runs = new List<(float, float)>();
        int? start = null;
        var width = (int)(page.Width * Scale);
        var text = Runs(page).Where(r => Math.Abs(r.Positions[0].Y - y) < 1).ToList();
        var (textLeft, textRight) = (text.Min(r => r.Positions[0].X) - 0.5f, text.Max(r => r.Positions[^1].X) + 10);
        for (var x = 0; x <= width; x++)
        {
            var at = (x + 0.5f) / Scale;
            var dark = x < width && (at < textLeft || at > textRight) && Dark(Pixel(page, at, y - 3));
            if (dark && start is null)
                start = x;
            else if (!dark && start is { } from)
            {
                runs.Add((from / (float)Scale, x / (float)Scale));
                start = null;
            }
        }
        return runs;
    }

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    private static GlyphRun Run(PageContent page, string text) => Runs(page).First(r => r.Text.Trim().StartsWith(text, StringComparison.Ordinal));

    private static float X(PageContent page, string text) => Run(page, text).Positions[0].X;

    private static float Baseline(PageContent page, string text) => Run(page, text).Positions[0].Y;

    private sealed class Tolerance(float within) : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => Math.Abs(a - b) <= within;

        public int GetHashCode(float value) => 0;
    }
}
