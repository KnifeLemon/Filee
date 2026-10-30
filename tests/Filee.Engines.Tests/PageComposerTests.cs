// The layout engine's page composer (Typesetting/PageComposer.cs): page geometry, page breaks and keep rules, headers
// and footers with page numbers, pictures laid out like characters, 한글's stored page breaks, tables, columns, footnotes
// and endnotes, and documents of other formats (TXT, Markdown, HTML, EPUB, FB2, MOBI) composed without losing text.
// Documents are built here and laid out with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts); without them the
// tests skip.

using System.Diagnostics;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class PageComposerTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>100 × 200 pt paper, 10 pt margins, 10 pt header and footer areas: a body of 80 × 160 pt from y = 20.</summary>
    private static readonly HPage Small = new(10000, 20000, 1000, 1000, 1000, 1000, 1000, 1000, 0);

    private const string Sentence = "Every paragraph flows on to the next page when the body is full.";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── Pages ─────────────────────────

    [Fact]
    public void The_body_starts_below_the_header_area_on_the_sections_paper()
    {
        var section = Section(Small, Paragraph("Top"));
        var page = Assert.Single(Compose(section).Pages);

        Assert.Equal((100f, 200f), (page.Width, page.Height));
        var run = Runs(page).Single();
        // 10 pt text at 160%: the baseline 8.5 pt below the body top (10 + 10), the line starting at the left margin.
        Assert.Equal(28.5f, run.Positions[0].Y, 0.01f);
        Assert.Equal(10f, run.Positions[0].X, 0.5f);
    }

    [Fact]
    public void Text_flows_on_to_new_pages_and_stays_inside_the_body()
    {
        var section = Section(Small, [.. Enumerable.Range(0, 12).Select(i => Paragraph($"{i}: {Sentence}"))]);

        var pages = Compose(section).Pages;

        Assert.True(pages.Count > 2);
        foreach (var page in pages)
            Assert.All(Runs(page), r => Assert.InRange(r.Positions[0].Y, 20f, 180f));
        Assert.Equal(Normalise(string.Concat(section.Blocks.OfType<HParagraph>().Select(TextOf))), Normalise(string.Concat(pages.SelectMany(Runs).Select(r => r.Text))));
    }

    [Fact]
    public void A_page_break_or_a_column_break_starts_a_page()
    {
        var section = Section(Small, Paragraph("one"), Paragraph("two"), Paragraph("three"));
        ((HParagraph)section.Blocks[1]).PageBreakBefore = true;
        ((HParagraph)section.Blocks[2]).ColumnBreakBefore = true;

        var pages = Compose(section).Pages;

        Assert.Equal(["one", "two", "three"], pages.Select(p => string.Concat(Runs(p).Select(r => r.Text))));
    }

    [Fact]
    public void Keep_with_next_moves_a_heading_to_the_page_of_its_paragraph()
    {
        // Nine 16 pt lines fill 144 of the body's 160 pt; the heading would fit, its paragraph's first line would not.
        var blocks = Enumerable.Range(0, 9).Select(i => Paragraph($"line {i}")).ToList();
        var heading = Paragraph("Heading", new HParaFormat(KeepWithNext: true));
        blocks.Add(heading);
        blocks.Add(Paragraph("Body"));

        var pages = Compose(Section(Small, [.. blocks])).Pages;

        Assert.Equal(2, pages.Count);
        Assert.Equal(["Heading", "Body"], Runs(pages[1]).Select(r => r.Text));
    }

    [Fact]
    public void Keep_lines_together_moves_a_paragraph_that_would_split()
    {
        var blocks = Enumerable.Range(0, 8).Select(i => Paragraph($"line {i}")).ToList();
        blocks.Add(Paragraph(Sentence + " " + Sentence, new HParaFormat(KeepLines: true)));

        var pages = Compose(Section(Small, [.. blocks])).Pages;

        Assert.Equal(2, pages.Count);
        Assert.Equal(8, Runs(pages[0]).Count());
        Assert.StartsWith("Every", Runs(pages[1]).First().Text);
    }

    [Fact]
    public void Widow_and_orphan_control_keeps_two_lines_on_either_page()
    {
        // Nine lines fill the first page but one; a five-line paragraph would leave one line there (an orphan).
        var blocks = Enumerable.Range(0, 9).Select(i => Paragraph($"line {i}")).ToList();
        blocks.Add(Paragraph(Sentence + " " + Sentence, new HParaFormat(WidowOrphan: true)));
        var plain = blocks.Take(9).Append(Paragraph(Sentence + " " + Sentence)).ToList();

        var guarded = Compose(Section(Small, [.. blocks])).Pages;
        var unguarded = Compose(Section(Small, [.. plain])).Pages;

        Assert.Equal(10, Runs(unguarded[0]).Count()); // one line of the paragraph left at the bottom
        Assert.Equal(9, Runs(guarded[0]).Count());
    }

    // ───────────────────────── Headers, footers and page numbers ─────────────────────────

    [Fact]
    public void Headers_and_footers_follow_odd_and_even_pages_and_skip_a_hidden_first_page()
    {
        var section = Section(Small, Paragraph("one"), Paragraph("two"), Paragraph("three"));
        foreach (var paragraph in section.Blocks.OfType<HParagraph>().Skip(1))
            paragraph.PageBreakBefore = true;
        section.Headers.Add(HeaderFooter(HPageType.Both, "Head"));
        section.Footers.Add(HeaderFooter(HPageType.Odd, "odd ", HFieldKind.PageNumber, " of ", HFieldKind.TotalPages));
        section.Footers.Add(HeaderFooter(HPageType.Even, "even ", HFieldKind.PageNumber));
        section.HideFirstHeader = true;

        var pages = Compose(section).Pages;

        Assert.Equal(["one", "odd1of3"], Texts(pages[0]));
        Assert.Equal(["two", "Head", "even2"], Texts(pages[1]));
        Assert.Equal(["three", "Head", "odd3of3"], Texts(pages[2]));
        // The header in the header area (from 10 pt), the footer in the footer area (from 180 pt).
        Assert.InRange(Runs(pages[1]).Single(r => r.Text == "Head").Positions[0].Y, 10f, 20f);
        Assert.InRange(Runs(pages[1]).Single(r => r.Text.StartsWith("even")).Positions[0].Y, 180f, 190f);
    }

    [Fact]
    public void Page_numbers_count_on_across_sections_unless_one_restarts_them()
    {
        var first = Section(Small, Paragraph("a"), Paragraph("b"));
        ((HParagraph)first.Blocks[1]).PageBreakBefore = true;
        var second = Section(Small, Paragraph("c"));
        var third = Section(Small, Paragraph("d"));
        third.StartPageNumber = 10;
        foreach (var section in new[] { first, second, third })
            section.Footers.Add(HeaderFooter(HPageType.Both, "p", HFieldKind.PageNumber));
        var document = new HDocument();
        document.Sections.AddRange([first, second, third]);

        var pages = Compose(document).Pages;

        Assert.Equal(["p1", "p2", "p3", "p10"], pages.Select(p => Texts(p)[^1]));
    }

    [Fact]
    public void A_total_pages_field_in_the_body_shows_the_count()
    {
        var count = Paragraph("pages: ");
        count.Inlines.Add(new HField(HFieldKind.TotalPages, Format()));
        var next = Paragraph("next");
        next.PageBreakBefore = true;

        var pages = Compose(Section(Small, count, next)).Pages;

        Assert.Equal("pages:2", Texts(pages[0])[0].Replace(" ", ""));
    }

    // ───────────────────────── Pictures and stored pages ─────────────────────────

    [Fact]
    public void A_picture_laid_out_like_a_character_takes_its_size_on_the_line()
    {
        var path = Path.Combine(fx.NewFolder(), "picture.png");
        using (var bitmap = new SKBitmap(40, 30))
        {
            bitmap.Erase(SKColors.Red);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        var paragraph = Paragraph("before ");
        paragraph.Inlines.Add(new HImage(path) { Width = 4000, Height = 3000 });
        paragraph.Inlines.Add(new HText(" after", Format()));
        paragraph.Inlines.Add(new HImage(path)); // its own size: 40 × 30 px at 96 dpi

        var page = Assert.Single(Compose(Section(Small with { Width = 30000 }, paragraph)).Pages);

        Assert.Equal(2, page.Items.OfType<DrawingItem>().Count());
        var runs = Runs(page).ToList();
        var after = runs.Single(r => r.Text.Contains("after"));
        var before = runs.Single(r => r.Text.Contains("before"));
        Assert.True(after.Positions[0].X - before.Positions[^1].X >= 40, "the picture's 40 pt between the words");
        // The picture stands on the baseline: the line is at least 30 pt high above it.
        Assert.True(before.Positions[0].Y >= 20 + 30);
    }

    [Fact]
    public void Hangul_stored_page_starts_and_places_are_followed()
    {
        // Three one-line paragraphs on their stored layout: the second 50 pt down, the third starting a page.
        HParagraph Stored(string text, int top, bool pageStart)
        {
            var paragraph = Paragraph(text);
            paragraph.Lines = new HLineLayout([new HLineSegment(0, top, 1000, 1000, 850, 600, 0, 8000, pageStart ? 1 : 0)], false, [0]);
            return paragraph;
        }

        var pages = Compose(Section(Small, Stored("one", 0, false), Stored("two", 5000, false), Stored("three", 0, true))).Pages;

        Assert.Equal(2, pages.Count);
        Assert.Equal(20 + 50 + 8.5f, Runs(pages[0]).Single(r => r.Text == "two").Positions[0].Y, 0.01f);
        Assert.Equal(["three"], Texts(pages[1]));
    }

    // ───────────────────────── Tables ─────────────────────────

    [Fact]
    public void Table_columns_take_absolute_relative_or_equal_widths()
    {
        var absolute = Table("a1", "b1");
        absolute.ColumnWidths = [4000, 12000];
        var relative = Table("a2", "b2", "c2");
        relative.RelativeWidths = [0.5, 0, 0];
        var equal = Table("a3", "b3", "c3", "d3");

        // A body 280 pt wide; every cell starts its text after the same padding.
        var page = Assert.Single(Compose(Section(Small with { Width = 30000 }, absolute, relative, equal)).Pages);

        float X(string text) => Runs(page).Single(r => r.Text == text).Positions[0].X;
        Assert.Equal(40, X("b1") - X("a1"), 0.05f);
        Assert.Equal(140, X("b2") - X("a2"), 0.05f);
        Assert.Equal(70, X("c2") - X("b2"), 0.05f);
        Assert.Equal(70, X("b3") - X("a3"), 0.05f);
    }

    [Fact]
    public void A_wide_table_of_another_format_is_scaled_to_the_text_width_and_hangul_keeps_its_widths()
    {
        var table = Table("left", "right");
        table.ColumnWidths = [10000, 10000]; // 200 pt in an 80 pt body
        var document = new HDocument();
        document.Sections.Add(Section(Small, table));

        var scaled = new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate()).Compose(document, TestContext.Current.CancellationToken).Pages[0];
        var kept = Compose(document).Pages[0];

        Assert.Equal(40, Runs(scaled).Single(r => r.Text == "right").Positions[0].X - Runs(scaled).Single(r => r.Text == "left").Positions[0].X, 0.5f);
        Assert.Equal(100, Runs(kept).Single(r => r.Text == "right").Positions[0].X - Runs(kept).Single(r => r.Text == "left").Positions[0].X, 0.5f);
    }

    [Fact]
    public void Rows_are_as_high_as_their_tallest_cell_and_cells_align_their_content()
    {
        var table = Table("top", "one two three four five six");
        table.ColumnWidths = [4000, 4000];
        table.Rows[0].Cells[0].VerticalAlign = HVerticalAlign.Bottom;
        var page = Assert.Single(Compose(Section(Small, table)).Pages);

        // The long cell wraps into several 16 pt lines; the short one sits at the row's bottom, on its last line.
        var lines = Runs(page).Where(r => r.Text != "top").Select(r => r.Positions[0].Y).Distinct().OrderBy(y => y).ToList();
        Assert.True(lines.Count > 2);
        Assert.Equal(lines[^1], Runs(page).Single(r => r.Text == "top").Positions[0].Y, 0.5f);
    }

    [Fact]
    public void A_cell_spanning_rows_stretches_them_and_the_next_cell_skips_its_column()
    {
        var table = new HTable { ColumnCount = 2, ColumnWidths = [4000, 4000] };
        var first = new HRow();
        first.Cells.Add(Cell(Sentence, rowSpan: 2));
        first.Cells.Add(Cell("b"));
        var second = new HRow();
        second.Cells.Add(Cell("c")); // auto-placed in column 1, the first column taken by the span
        table.Rows.AddRange([first, second]);

        var page = Assert.Single(Compose(Section(Small, table)).Pages);

        var b = Runs(page).Single(r => r.Text == "b").Positions[0];
        var c = Runs(page).Single(r => r.Text == "c").Positions[0];
        Assert.Equal(b.X, c.X, 0.01f);
        Assert.True(Runs(page).Where(r => r.Text != "b" && r.Text != "c").Max(r => r.Positions[0].Y) > c.Y, "the spanning cell reaches into the second row");
    }

    [Fact]
    public void A_table_that_must_not_split_moves_to_the_next_page_whole()
    {
        var table = Rows(4, "row");
        table.PageBreak = HTablePageBreak.None;
        HBlock[] blocks = [.. Enumerable.Range(0, 7).Select(i => Paragraph($"line {i}")), table];

        var pages = Compose(Section(Small, blocks)).Pages;

        Assert.Equal(2, pages.Count);
        Assert.Equal(4, Runs(pages[1]).Count(r => r.Text.StartsWith("row")));
    }

    [Fact]
    public void A_table_splits_between_rows_and_repeats_its_header_rows()
    {
        var table = Rows(12, "row");
        table.Rows.Insert(0, Row(Cell("Head")));
        table.Rows[0].Header = true;
        table.PageBreak = HTablePageBreak.Cell;

        var pages = Compose(Section(Small, table)).Pages;

        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.Equal("Head", Texts(page)[0]));
        Assert.Equal(12, pages.Sum(p => Runs(p).Count(r => r.Text.StartsWith("row"))));
    }

    [Fact]
    public void A_row_taller_than_the_page_left_splits_inside_its_cells_unless_it_must_not()
    {
        var text = string.Join(" ", Enumerable.Repeat(Sentence, 4));
        HBlock[] Blocks(bool cantSplit)
        {
            var table = new HTable { ColumnCount = 1, ColumnWidths = [8000] };
            table.Rows.Add(Row(Cell(text)));
            table.Rows[0].CantSplit = cantSplit;
            return [.. Enumerable.Range(0, 6).Select(i => Paragraph($"line {i}")), table];
        }

        var split = Compose(Section(Small, Blocks(cantSplit: false))).Pages;
        var whole = Compose(Section(Small, Blocks(cantSplit: true))).Pages;

        Assert.True(Runs(split[0]).Count() > 6, "part of the row on the first page");
        Assert.Equal(6, Runs(whole[0]).Count());
    }

    [Fact]
    public void A_table_in_a_cell_is_laid_out_inside_it()
    {
        var inner = Table("x", "y");
        var outer = new HTable { ColumnCount = 1, ColumnWidths = [8000] };
        var cell = new HCell();
        cell.Blocks.Add(inner);
        outer.Rows.Add(Row(cell));

        var page = Assert.Single(Compose(Section(Small, outer)).Pages);

        var x = Runs(page).Single(r => r.Text == "x").Positions[0];
        var y = Runs(page).Single(r => r.Text == "y").Positions[0];
        Assert.Equal(x.Y, y.Y, 0.01f);
        Assert.True(y.X > x.X);
    }

    [Fact]
    public void A_table_hangul_keeps_in_an_empty_paragraph_takes_that_line_its_margin_and_offset()
    {
        // The model's empty part of the 한글 paragraph before the table takes no line; the table keeps 1 pt of outer
        // margin and a 5 pt offset from its paragraph.
        var holder = Paragraph("");
        holder.Inlines.Clear();
        holder.Lines = new HLineLayout([new HLineSegment(0, 0, 1000, 1000, 850, 600, 0, 8000, 0)], false);
        var table = Table("cell");
        table.OuterMargin = new HInsets(0, 0, 100, 100);
        table.VerticalOffset = 500;

        var page = Assert.Single(Compose(Section(Small, holder, table)).Pages);

        // Body top 20 + offset 5 + margin 1 + padding 1.41 + baseline 8.5.
        Assert.Equal(20 + 5 + 1 + 1.41f + 8.5f, Runs(page).Single().Positions[0].Y, 0.05f);
    }

    // ───────────────────────── Columns ─────────────────────────

    /// <summary>Two columns 35 pt wide with a 10 pt gap: the second starts at x = 55.</summary>
    private static readonly HColumns Two = new(2, 1000, false);

    [Fact]
    public void Text_fills_a_column_then_the_next_and_the_section_end_leaves_them_as_they_are()
    {
        var section = Section(Small, [.. Enumerable.Range(0, 14).Select(i => Paragraph($"p{i}"))]);
        section.Columns = Two;

        var page = Assert.Single(Compose(section).Pages);

        var left = Runs(page).Where(r => r.Positions[0].X < 30).Select(r => r.Text).ToList();
        var right = Runs(page).Where(r => r.Positions[0].X > 50).Select(r => r.Text).ToList();
        // Fourteen 16 pt lines: ten fit in a column, the other four start the next (한글 doesn't balance at the end).
        Assert.Equal([.. Enumerable.Range(0, 10).Select(i => $"p{i}")], left);
        Assert.Equal([.. Enumerable.Range(10, 4).Select(i => $"p{i}")], right);
        Assert.Equal(55f, Runs(page).First(r => r.Text == "p10").Positions[0].X, 0.5f);
        Assert.Equal(Runs(page).First(r => r.Text == "p0").Positions[0].Y, Runs(page).First(r => r.Text == "p10").Positions[0].Y, 0.01f);
    }

    [Fact]
    public void Columns_flow_on_to_the_next_page_after_the_last_column()
    {
        var section = Section(Small, [.. Enumerable.Range(0, 24).Select(i => Paragraph($"p{i}"))]);
        section.Columns = Two;

        var pages = Compose(section).Pages;

        Assert.Equal(2, pages.Count);
        // The full page holds ten lines a column; the last page's four stay in its first.
        Assert.Equal(20, Runs(pages[0]).Count());
        Assert.Equal(["p20", "p21", "p22", "p23"], Runs(pages[1]).Where(r => r.Positions[0].X < 30).Select(r => r.Text));
    }

    [Fact]
    public void A_column_break_starts_the_next_column()
    {
        var section = Section(Small, Paragraph("one"), Paragraph("two"), Paragraph("three"));
        section.Columns = Two;
        ((HParagraph)section.Blocks[1]).ColumnBreakBefore = true;

        var page = Assert.Single(Compose(section).Pages);

        Assert.Equal(["one"], Runs(page).Where(r => r.Positions[0].X < 30).Select(r => r.Text));
        Assert.Equal(["two", "three"], Runs(page).Where(r => r.Positions[0].X > 50).Select(r => r.Text));
    }

    [Fact]
    public void A_separated_section_draws_a_line_between_its_columns()
    {
        HSection Of(bool separator)
        {
            var section = Section(Small, [.. Enumerable.Range(0, 4).Select(i => Paragraph($"p{i}"))]);
            section.Columns = Two with { Separator = separator };
            return section;
        }

        var plain = Assert.Single(Compose(Of(false)).Pages).Items.OfType<DrawingItem>().Count();
        var separated = Assert.Single(Compose(Of(true)).Pages).Items.OfType<DrawingItem>().Count();

        Assert.Equal(plain + 1, separated);
    }

    [Fact]
    public void Columns_changing_on_a_page_start_under_the_balanced_columns_before()
    {
        var blocks = Enumerable.Range(0, 5).Select(i => Paragraph($"a{i}")).ToList();
        var wide = Paragraph("wide");
        wide.ColumnsChange = HColumns.Single;
        blocks.Add(wide);
        var section = Section(Small, [.. blocks]);
        section.Columns = Two;

        var page = Assert.Single(Compose(section).Pages);

        // a0 a1 a2 | a3 a4 (the first column takes the odd line, as in 한글), then the one-column paragraph under both,
        // at the left margin.
        var runs = Runs(page).ToDictionary(r => r.Text);
        Assert.Equal(runs["a0"].Positions[0].Y, runs["a3"].Positions[0].Y, 0.01f);
        Assert.Equal(55f, runs["a4"].Positions[0].X, 0.5f);
        Assert.Equal(runs["a2"].Positions[0].Y + 16, runs["wide"].Positions[0].Y, 0.01f);
        Assert.Equal(10f, runs["wide"].Positions[0].X, 0.5f);
    }

    // ───────────────────────── Notes ─────────────────────────

    [Fact]
    public void A_footnote_goes_to_the_foot_of_the_page_its_reference_is_on()
    {
        var paragraph = Paragraph("Text");
        paragraph.Inlines.Add(Note(endnote: false, "Note"));
        var section = Section(Small, paragraph, Paragraph("After"));

        var page = Assert.Single(Compose(section).Pages);

        var runs = Runs(page).ToList();
        // The reference shows the number, raised and smaller, right after the text.
        var reference = runs.First(r => r.Text == "1)");
        Assert.True(reference.Size < 10);
        Assert.True(reference.Positions[0].Y < runs.First(r => r.Text == "Text").Positions[0].Y);
        // The note's line ends at the body's bottom (180 pt), its number in front.
        var note = runs.Single(r => r.Text.Contains("Note"));
        Assert.InRange(note.Positions[0].Y, 170f, 180f);
        Assert.Contains("1)", Texts(page)[^1]);
        Assert.Contains(page.Items, i => i is DrawingItem);
    }

    [Fact]
    public void A_line_whose_footnote_does_not_fit_moves_to_the_next_page_with_it()
    {
        // Nine lines take 144 of 160 pt: the tenth would fit alone, not with its note and the separator.
        var blocks = Enumerable.Range(0, 9).Select(i => Paragraph($"line {i}")).ToList();
        var last = Paragraph("Noted");
        last.Inlines.Add(Note(endnote: false, "Note"));
        blocks.Add(last);

        var pages = Compose(Section(Small, [.. blocks])).Pages;

        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain(Runs(pages[0]), r => r.Text.Contains("Note"));
        Assert.Contains(Runs(pages[1]), r => r.Text == "Noted");
        Assert.Contains(Runs(pages[1]), r => r.Text.Contains("Note") && r.Text != "Noted");
    }

    [Fact]
    public void Footnotes_and_endnotes_are_numbered_on_and_endnotes_start_a_page_after_the_body()
    {
        var section = NotesSection();

        var pages = Compose(section).Pages;

        // The raised references are a line of their own here (Texts groups by baseline): footnote 1 and endnote 1, then 2 and 2.
        Assert.Equal(["One", "1)1)", "Two", "2)2)", "Last", "1)FootA", "2)FootB"], Texts(pages[0]));
        Assert.Equal(["1)EndA", "2)EndB"], Texts(pages[1]));
    }

    [Fact]
    public void Endnotes_follow_the_text_when_the_section_says_so_in_its_number_format()
    {
        var section = NotesSection();
        section.Endnotes = HNoteShape.Endnote with { Format = new HNoteFormat("ROMAN_SMALL", "[", "]", false), BeneathText = true };
        section.Footnotes = HNoteShape.Footnote with { Format = new HNoteFormat("LATIN_CAPITAL", "", "", true) };

        var page = Assert.Single(Compose(section).Pages);

        Assert.Equal(["One", "A[i]", "Two", "B[ii]", "Last", "[i]EndA", "[ii]EndB", "AFootA", "BFootB"], Texts(page));
    }

    [Fact]
    public void Footnotes_stack_at_the_foot_of_the_body_as_hangul_sets_them()
    {
        var section = NotesSection();

        var page = Compose(section).Pages[0];

        // 한글's spacing: 2.83 pt between notes, each as high as its text (10 pt) without the line's spacing below, the
        // last ending at the body's bottom (180 pt); the separator 5.67 pt above the first, 5 cm long or the text width.
        var runs = Runs(page).ToList();
        var a = runs.First(r => r.Text.Contains("Foot A")).Positions[0].Y;
        var b = runs.First(r => r.Text.Contains("Foot B")).Positions[0].Y;
        Assert.Equal(12.83f, b - a, 0.01f);
        Assert.Equal(180f - 1.5f, b, 0.01f);
    }

    private static HSection NotesSection()
    {
        var first = Paragraph("One");
        first.Inlines.Add(Note(endnote: false, "Foot A"));
        first.Inlines.Add(Note(endnote: true, "End A"));
        var second = Paragraph("Two");
        second.Inlines.Add(Note(endnote: false, "Foot B"));
        second.Inlines.Add(Note(endnote: true, "End B"));
        return Section(Small, first, second, Paragraph("Last"));
    }

    // ───────────────────────── Other formats ─────────────────────────

    public static TheoryData<string> Formats => ["txt", "md", "html", "epub", "fb2", "mobi"];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Documents_of_other_formats_compose_without_losing_text(string format)
    {
        SkipWithoutFonts();
        var folder = fx.NewFolder();
        var path = Input(folder, format);
        var document = await DocumentReaders.ReadAsync(path, format, folder, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var composed = new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate()).Compose(document, ct);
        using var pdf = new MemoryStream();
        PdfPageWriter.Write(pdf, composed.Pages, cancellationToken: ct);

        var expected = string.Concat(Paragraphs(document.Sections.SelectMany(s => s.Blocks)).Select(TextOf));
        Assert.NotEmpty(Normalise(expected));
        var check = PdfChecks.TextRoundTrip(pdf.ToArray(), expected);
        Assert.True(check.Ok, check.ToString());
    }

    [Fact]
    public void A_novel_of_640_pages_composes_in_seconds()
    {
        SkipWithoutFonts();
        // A4 with 한글's margins holds some 40 lines of 45 Hangul syllables: some 650 pages of them.
        var section = new HSection { Page = PageComposer.DefaultPage };
        var sentence = "다람쥐 헌 쳇바퀴에 타고파. 문서 변환 도구는 문단의 줄 간격과 줄이 나뉘는 자리를 지켜야 합니다. ";
        for (var i = 0; i < 640 * 14; i++)
            section.Blocks.Add(Paragraph(string.Concat(Enumerable.Repeat(sentence, 3)), chars: Format() with { Font = RepositoryFonts.Korean }));
        var document = new HDocument();
        document.Sections.Add(section);

        var watch = Stopwatch.StartNew();
        var pages = new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken).Pages;
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"{pages.Count} pages in {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.InRange(pages.Count, 600, 900);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{pages.Count} pages took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private void SkipWithoutFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private ComposedDocument Compose(HSection section)
    {
        var document = new HDocument();
        document.Sections.Add(section);
        return Compose(document);
    }

    private ComposedDocument Compose(HDocument document)
    {
        SkipWithoutFonts();
        return new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken);
    }

    private static HSection Section(HPage page, params HBlock[] blocks)
    {
        var section = new HSection { Page = page };
        section.Blocks.AddRange(blocks);
        return section;
    }

    /// <summary>A one-row table with a cell per text, its columns sharing the width.</summary>
    private static HTable Table(params string[] texts)
    {
        var table = new HTable { ColumnCount = texts.Length };
        table.Rows.Add(Row([.. texts.Select(t => Cell(t))]));
        return table;
    }

    /// <summary>A one-column table of <paramref name="count"/> rows, "prefix 0", "prefix 1", ...</summary>
    private static HTable Rows(int count, string prefix)
    {
        var table = new HTable { ColumnCount = 1 };
        for (var i = 0; i < count; i++)
            table.Rows.Add(Row(Cell($"{prefix} {i}")));
        return table;
    }

    private static HRow Row(params HCell[] cells)
    {
        var row = new HRow();
        row.Cells.AddRange(cells);
        return row;
    }

    private static HCell Cell(string text, int rowSpan = 1)
    {
        var cell = new HCell { RowSpan = rowSpan };
        cell.Blocks.Add(Paragraph(text));
        return cell;
    }

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    private static HParagraph Paragraph(string text, HParaFormat format = default, HCharFormat? chars = null)
    {
        var paragraph = new HParagraph
        {
            Format = format with { Before = format.Before ?? 0, After = format.After ?? 0 },
            MarkFormat = chars ?? Format(),
        };
        paragraph.Inlines.Add(new HText(text, chars ?? Format()));
        return paragraph;
    }

    private static HNote Note(bool endnote, string text)
    {
        var note = new HNote(endnote);
        note.Blocks.Add(Paragraph(text));
        return note;
    }

    private static HHeaderFooter HeaderFooter(HPageType pages, params object[] content)
    {
        var header = new HHeaderFooter(pages);
        var paragraph = new HParagraph { Format = new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        foreach (var piece in content)
            paragraph.Inlines.Add(piece is HFieldKind kind ? new HField(kind, Format()) : new HText((string)piece, Format()));
        header.Blocks.Add(paragraph);
        return header;
    }

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    /// <summary>The text of each line of a page, top to bottom, without spaces.</summary>
    private static List<string> Texts(PageContent page) =>
        [.. Runs(page).GroupBy(r => MathF.Round(r.Positions[0].Y, 1)).Select(g => Normalise(string.Concat(g.Select(r => r.Text))))];

    private static string Normalise(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    private static string TextOf(HParagraph paragraph) =>
        string.Concat(paragraph.Inlines.Select(i => i switch
        {
            HText text => text.Text,
            HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
            _ => "",
        }));

    private static IEnumerable<HParagraph> Paragraphs(IEnumerable<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is HParagraph paragraph)
                yield return paragraph;
            else if (block is HTable table)
                foreach (var inner in Paragraphs(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).Concat(table.Caption)))
                    yield return inner;
        }
    }

    /// <summary>A document of <paramref name="format"/> written into <paramref name="folder"/>.</summary>
    private static string Input(string folder, string format)
    {
        const string Body = "첫째 문단입니다. Filee composes every paragraph.";
        var path = Path.Combine(folder, "input." + format);
        switch (format)
        {
            case "txt":
                File.WriteAllText(path, $"{Body}\n\n둘째 문단: {Sentence}\n", new UTF8Encoding(false));
                return path;
            case "md":
                File.WriteAllText(path, $"# 제목\n\n{Body}\n\n- 항목 하나\n- item two\n\n> {Sentence}\n", new UTF8Encoding(false));
                return path;
            case "html":
                File.WriteAllText(path, $"<html><body><h1>제목</h1><p>{Body}</p><p><a href=\"https://filee.sh\">링크</a> {Sentence}</p></body></html>", new UTF8Encoding(false));
                return path;
            case "epub":
                return EbookBuilders.Epub(folder);
            case "fb2":
                return EbookBuilders.Fb2(folder);
            default:
                File.WriteAllBytes(path, EbookBuilders.Mobi($"<html><body><p>{Body}</p><p>{Sentence}</p></body></html>", [], new EbookBuilders.MobiOptions()));
                return path;
        }
    }
}
