// Word list numbers, first-line tab stops and page borders in the layout engine: a number centred on or ending at its
// place (w:lvlJc), a tab stop between a hanging first line's start and the left indent, the number's own format (the
// paragraph mark's with the level's w:rPr over it) and a page border in front of or behind the text (w:pgBorders
// zOrder). DOCX built in code, read by DocxReader and composed as the renderer composes Word documents; laid out with
// the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts), without which the layout tests skip.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordListLayoutTests : IDisposable
{
    /// <summary>The left margin of <see cref="DefaultPage"/> in points.</summary>
    private const float Margin = 72;

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-word-lists").FullName;

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

    // ───────────────────────── Number alignment ─────────────────────────

    [Fact]
    public void A_number_starts_centres_on_or_ends_at_its_place()
    {
        // The number's place is the first line's start, 36 pt in (left 1440 less hanging 720); the text at the left
        // indent, the hanging indent's stop, 72 pt in, whatever the alignment.
        var item = Item("Item", "<w:ind w:left=\"1440\" w:hanging=\"720\"/>");
        var left = ComposeOne(item, Numbering("decimal", "%1.", "left"));
        var centre = ComposeOne(item, Numbering("decimal", "%1.", "center"));
        var right = ComposeOne(item, Numbering("decimal", "%1.", "right"));

        Assert.Equal(Margin + 36, Left(left, "1."), 0.1f);
        var width = Left(left, "1.") - Left(right, "1.");
        Assert.True(width > 3, $"The right-aligned number ends at its place: it starts {width} pt before it.");
        Assert.Equal(Margin + 36 - width / 2, Left(centre, "1."), 0.1f);
        foreach (var page in (PageContent[])[left, centre, right])
            Assert.Equal(Margin + 72, Left(page, "Item"), 0.1f);
    }

    [Fact]
    public void The_reader_keeps_the_number_alignment()
    {
        var levels = Levels(Read(Item("Item", ""), Numbering("decimal", "%1.", "right")));

        Assert.Equal(HAlign.Right, levels[0].Align);
    }

    // ───────────────────────── Tab stops on a hanging first line ─────────────────────────

    [Fact]
    public void A_tab_stop_between_the_first_lines_start_and_the_left_indent_counts_on_the_first_line()
    {
        // First line at 0, left indent at 72 pt, a stop at 50 pt: the tab goes there, not to the hanging indent.
        var page = ComposeOne(P(Run("a") + "<w:r><w:tab/></w:r>" + Run("b"), "<w:tabs><w:tab w:val=\"left\" w:pos=\"1000\"/></w:tabs><w:ind w:left=\"1440\" w:hanging=\"1440\"/>"));

        Assert.Equal(Margin, Left(page, "a"), 0.1f);
        Assert.Equal(Margin + 50, Left(page, "b"), 0.1f);
    }

    [Fact]
    public void The_tab_after_a_number_goes_to_a_stop_before_the_left_indent()
    {
        var page = ComposeOne(Item("Item", "<w:tabs><w:tab w:val=\"left\" w:pos=\"1000\"/></w:tabs><w:ind w:left=\"1440\" w:hanging=\"1440\"/>"),
            Numbering("decimal", "%1.", "left"));

        Assert.Equal(Margin, Left(page, "1."), 0.1f);
        Assert.Equal(Margin + 50, Left(page, "Item"), 0.1f);
    }

    [Fact]
    public void Lines_after_the_first_leave_a_stop_before_the_left_indent_out()
    {
        // The second line starts at the left indent (72 pt): its tab goes to the next default stop (every 36 pt from
        // the margin), 108 pt, not back to the stop at 50 pt.
        var page = ComposeOne(P(Run("a") + "<w:r><w:br/></w:r>" + Run("c") + "<w:r><w:tab/></w:r>" + Run("d"),
            "<w:tabs><w:tab w:val=\"left\" w:pos=\"1000\"/></w:tabs><w:ind w:left=\"1440\" w:hanging=\"1440\"/>"));

        Assert.Equal(Margin + 72, Left(page, "c"), 0.1f);
        Assert.Equal(Margin + 108, Left(page, "d"), 0.1f);
    }

    // ───────────────────────── The number's format ─────────────────────────

    [Fact]
    public void A_number_is_drawn_in_its_levels_format_over_the_paragraph_marks()
    {
        // The mark is 14 pt, the text 10 pt; the level makes the number red. Word draws the number 14 pt red.
        var numbering = Numbering("decimal", "%1.", "left", "<w:rPr><w:color w:val=\"FF0000\"/></w:rPr>");
        var page = ComposeOne(Item("Item", "<w:ind w:left=\"720\" w:hanging=\"720\"/><w:rPr><w:sz w:val=\"28\"/></w:rPr>"), numbering);

        var number = Run(page, "1.");
        Assert.Equal(14, number.Size, 0.01f);
        Assert.Equal(SkiaSharp.SKColors.Red, number.Color);
        var text = Run(page, "Item");
        Assert.Equal(10, text.Size, 0.01f);
        Assert.Equal(SkiaSharp.SKColors.Black, text.Color);
    }

    [Fact]
    public void A_bullet_takes_its_levels_size_but_keeps_its_unicode_character()
    {
        var numbering = Numbering("bullet", "", "left", "<w:rPr><w:rFonts w:ascii=\"Symbol\" w:hAnsi=\"Symbol\" w:hint=\"default\"/><w:sz w:val=\"36\"/></w:rPr>");
        var page = ComposeOne(Item("Item", "<w:ind w:left=\"720\" w:hanging=\"720\"/>"), numbering);

        var bullet = Run(page, "•");
        Assert.Equal(18, bullet.Size, 0.01f);
        Assert.Equal(10, Run(page, "Item").Size, 0.01f);
    }

    [Fact]
    public void The_number_format_takes_no_part_in_the_lists_equality()
    {
        var levels = Read(Item("Item", "<w:rPr><w:b/></w:rPr>") + Item("Next", ""), Numbering("decimal", "%1.", "left"));
        var lists = levels.Sections[0].Blocks.OfType<HParagraph>().Select(p => p.List).OfType<HListRef>().ToList();

        Assert.Equal(2, lists.Count);
        Assert.NotEqual(lists[0].MarkerFormat, lists[1].MarkerFormat);
        Assert.Equal(lists[0], lists[1]);
    }

    // ───────────────────────── Page borders in front of the text ─────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("front", true)]
    [InlineData("back", false)]
    public void A_page_border_goes_in_front_of_the_text_unless_it_says_back(string? zOrder, bool front)
    {
        var order = zOrder is null ? "" : $" w:zOrder=\"{zOrder}\"";
        var builder = new DocxBuilder
        {
            SectionProperties = DefaultPage + $"<w:pgBorders w:offsetFrom=\"page\"{order}><w:left w:val=\"single\" w:sz=\"16\" w:space=\"24\"/></w:pgBorders>",
        }.Paragraph(P(Run("text")));
        var document = DocxReader.Read(builder.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx")), Path.Combine(_folder, "media"));
        Assert.Equal(front, Assert.Single(document.Sections[0].PageBorders).InFront);

        var items = Compose(document).Items.ToList();
        // The page holds the text and the border's drawing: after the text when in front, before it (under) when not.
        var text = items.FindLastIndex(i => i is GlyphItem);
        Assert.True(text >= 0);
        Assert.Equal(front, items.Skip(text).Any(i => i is DrawingItem));
        Assert.Equal(!front, items.Take(text).Any(i => i is DrawingItem));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string Run(string text) => R(text, $"<w:rFonts w:ascii=\"{RepositoryFonts.Latin}\" w:hAnsi=\"{RepositoryFonts.Latin}\"/><w:sz w:val=\"20\"/>");

    /// <summary>An item of list 1 (level 0) holding <paramref name="text"/>, with more paragraph properties.</summary>
    private static string Item(string text, string pPr) => P(Run(text), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>" + pPr);

    /// <summary>List 1: one level of <paramref name="format"/> showing <paramref name="text"/>, aligned by <paramref name="jc"/>.</summary>
    private static string Numbering(string format, string text, string jc, string extra = "") =>
        $"<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/>" +
        $"<w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"{jc}\"/>{extra}</w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num>";

    private HDocument Read(string body, string? numbering = null)
    {
        var builder = new DocxBuilder { Settings = Word2013Settings, Numbering = numbering }.Paragraph(body);
        return DocxReader.Read(builder.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx")), Path.Combine(_folder, "media"));
    }

    private static List<HNumberingLevel> Levels(HDocument document) =>
        document.Sections[0].Blocks.OfType<HParagraph>().First(p => p.List is not null).List!.Numbering.Levels;

    private PageContent ComposeOne(string body, string? numbering = null) => Compose(Read(body, numbering));

    private PageContent Compose(HDocument document)
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages);
    }

    private static List<GlyphRun> Runs(PageContent page) => [.. page.Items.OfType<GlyphItem>().Select(i => i.Run)];

    private static GlyphRun Run(PageContent page, string text) => Runs(page).First(r => r.Text.StartsWith(text, StringComparison.Ordinal));

    /// <summary>Where the first run that starts with <paramref name="text"/> starts.</summary>
    private static float Left(PageContent page, string text) => Run(page, text).Positions[0].X;
}
