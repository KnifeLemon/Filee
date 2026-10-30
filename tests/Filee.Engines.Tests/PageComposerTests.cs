// The layout engine's page composer (Typesetting/PageComposer.cs): page geometry, page breaks and keep rules, headers
// and footers with page numbers, pictures laid out like characters, 한글's stored page breaks, and documents of other
// formats (TXT, Markdown, HTML, EPUB, FB2, MOBI) composed without losing text. Documents are built here and laid out
// with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts); without them the tests skip.

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

    private static HSection Section(HPage page, params HParagraph[] paragraphs)
    {
        var section = new HSection { Page = page };
        section.Blocks.AddRange(paragraphs);
        return section;
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
