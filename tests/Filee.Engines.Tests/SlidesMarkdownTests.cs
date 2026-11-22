// PowerPoint → Markdown one section per slide, as reveal.js reads it (issue #1702): "---" between slides, titles as
// headings, lists at their levels, links and emphasis, tables, pictures next to the file, speaker notes after "Note:",
// and slides without text kept. Presentations are built with PptxBuilder.

using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office.Slides;

namespace Filee.Engines.Tests;

public class SlidesMarkdownTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Markdown, string Path)> ConvertAsync(PptxBuilder deck, Preset? preset = null)
    {
        var pptx = deck.Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
        var job = await fx.ConvertAsync([pptx], preset ?? new Preset { TargetFormat = "md" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        var output = job.Outputs.Single(o => o.EndsWith(".md", StringComparison.Ordinal));
        return ((await File.ReadAllTextAsync(output, Ct)).ReplaceLineEndings("\n"), output);
    }

    private static string P(params string[] runs) => $"<a:p>{string.Concat(runs)}</a:p>";

    private static string P(int level, string bullet, params string[] runs) => $"<a:p><a:pPr lvl=\"{level}\">{bullet}</a:pPr>{string.Concat(runs)}</a:p>";

    [Fact]
    public void Markdown_comes_straight_from_the_slides_not_through_hwpx()
    {
        var route = fx.Catalog.CreatePlanner(fx.Converters.Select(c => c.Id).ToArray()).Plan("pptx", "md");

        Assert.NotNull(route);
        Assert.Equal("slides-md", Assert.Single(route.Steps).Converter.Id);
    }

    [Fact]
    public async Task Slides_become_sections_with_headings_and_lists()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.CenterTitle("Introduction") + deck.Body(("Welcome to the presentation.", 0)));
        deck.Slide(deck.Title("Three Key Points") + deck.Body(("First point", 0), ("Second point", 0), ("A detail", 1), ("Third point", 0)));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Equal(
            "# Introduction\n\n- Welcome to the presentation.\n\n---\n\n" +
            "## Three Key Points\n\n- First point\n- Second point\n  - A detail\n- Third point\n",
            markdown);
    }

    [Fact]
    public async Task Text_boxes_keep_paragraphs_numbered_lists_links_and_emphasis()
    {
        var deck = new PptxBuilder();
        var site = deck.Link("https://revealjs.com/markdown/");
        deck.Slide(deck.Title("Details") + deck.TextBox(
            P(PptxBuilder.Run("Read "), PptxBuilder.Run("the guide", link: site), PptxBuilder.Run(" first.")) +
            P(PptxBuilder.Run("Bold", bold: true), PptxBuilder.Run(" and "), PptxBuilder.Run("italic", italic: true)) +
            P(0, "<a:buAutoNum type=\"arabicPeriod\"/>", PptxBuilder.Run("One")) +
            P(0, "<a:buAutoNum type=\"arabicPeriod\"/>", PptxBuilder.Run("Two")) +
            P(PptxBuilder.Run("# not a heading"))));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Contains("Read [the guide](https://revealjs.com/markdown/) first.", markdown);
        Assert.Contains("**Bold** and *italic*", markdown);
        Assert.Contains("1. One\n2. Two", markdown);
        Assert.Contains("\\# not a heading", markdown);
        Assert.DoesNotContain("- Read", markdown); // a text box has no bullets of its own
    }

    [Fact]
    public async Task A_body_paragraph_without_a_bullet_stays_a_paragraph()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("Quote") + deck.RawBody(P(0, "<a:buNone/>", PptxBuilder.Run("Simplicity is prerequisite for reliability."))));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Equal("## Quote\n\nSimplicity is prerequisite for reliability.\n", markdown);
    }

    [Fact]
    public async Task Tables_pictures_notes_and_empty_slides_are_kept()
    {
        var deck = new PptxBuilder();
        var picture = deck.Image(await File.ReadAllBytesAsync(EngineFixture.MakeImage(fx.NewFolder(), "chart.png", ImageMagick.MagickFormat.Png), Ct));
        deck.Slide(deck.Title("Numbers") + deck.Table(838200, 1825625, 2000000, 400000, ["Region", "Sales"], ["North", "12 | 3"], ["South", "9"]));
        deck.Slide(deck.Picture(picture, 0, 0, PptxBuilder.Width, PptxBuilder.Height)).Notes("Pause here.", "Ask for questions.");
        deck.Slide("");
        deck.Slide(deck.Title("The end"));

        var (markdown, output) = await ConvertAsync(deck);

        var slides = markdown.Split("\n---\n");
        Assert.Equal(4, slides.Length);
        Assert.Contains("| Region | Sales |\n| --- | --- |\n| North | 12 \\| 3 |\n| South | 9 |", slides[0]);
        Assert.Matches(@"!\[Picture\]\(deck_files/img0001\.png\)", slides[1]);
        Assert.Contains("Note:\nPause here.\nAsk for questions.", slides[1]);
        Assert.Equal("", slides[2].Trim());
        Assert.Equal("## The end", slides[3].Trim());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "deck_files", "img0001.png")));
    }

    [Fact]
    public async Task Leave_out_pictures_writes_no_picture_folder()
    {
        var deck = new PptxBuilder();
        var picture = deck.Image(await File.ReadAllBytesAsync(EngineFixture.MakeImage(fx.NewFolder(), "chart.png", ImageMagick.MagickFormat.Png), Ct));
        deck.Slide(deck.Title("Chart") + deck.Picture(picture, 0, 0, 1000000, 1000000));

        var (markdown, output) = await ConvertAsync(deck, new Preset { TargetFormat = "md", Document = { LeaveOutPictures = true } });

        Assert.Equal("## Chart\n", markdown);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(output)!, "deck_files")));
    }

    [Fact]
    public async Task Right_to_left_and_other_scripts_come_through_as_they_are()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("שלום עולם") + deck.Body(("مرحبا بالعالم", 0), ("안녕하세요", 0)));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Equal("## שלום עולם\n\n- مرحبا بالعالم\n- 안녕하세요\n", markdown);
    }

    [Fact]
    public void A_file_that_isnt_a_presentation_says_so()
    {
        var path = Path.Combine(fx.NewFolder(), "fake.pptx");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("hello.txt");

        var error = Assert.Throws<InvalidDataException>(() => SlidesMarkdown.Write(path, _ => null, fx.NewFolder(), Ct));
        Assert.Contains("presentation.xml", error.Message);
    }

    [Fact]
    public async Task Slide_break_lines_in_body_and_notes_are_escaped()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("Breaks") + deck.TextBox(
            P(PptxBuilder.Run("---")) +
            P(PptxBuilder.Run("- - -")) +
            P(PptxBuilder.Run("-- -")))).Notes("Before", "---", "After");
        deck.Slide(deck.Title("Next"));

        var (markdown, _) = await ConvertAsync(deck);

        var slides = markdown.Split("\n\n---\n\n");
        Assert.Equal(2, slides.Length);
        Assert.Equal(1, markdown.Split('\n').Count(line => line == "---"));
        Assert.Contains("\n\n\\---\n\n", slides[0]);
        Assert.Contains("Note:\nBefore\n\\---\nAfter", slides[0]);
    }

    [Fact]
    public async Task Notes_markers_in_body_and_notes_are_escaped()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("Remarks") + deck.TextBox(
            P(PptxBuilder.Run("Note: x")) +
            P(PptxBuilder.Run("notes: y")))).Notes("NOTE: z", "Keep talking.");

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Contains("Note\\: x", markdown);
        Assert.Contains("notes\\: y", markdown);
        Assert.Contains("NOTE\\: z", markdown);
        var noteLines = markdown.Split('\n').Where(line => Regex.IsMatch(line, @"^\s*notes?:", RegexOptions.IgnoreCase)).ToList();
        Assert.Equal("Note:", Assert.Single(noteLines));
    }

    [Fact]
    public async Task List_item_line_starts_are_escaped()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("Priorities") + deck.Body(("# 1 priority", 0), ("1. step", 0)));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Contains("- \\# 1 priority\n- 1\\. step", markdown);
    }

    [Fact]
    public async Task Merged_table_cells_keep_columns_aligned()
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("Merged") + deck.RawTable(
            838200, 1825625, 2000000, 400000,
            [PptxBuilder.TableCell("A"), PptxBuilder.TableCell("B"), PptxBuilder.TableCell("C")],
            [PptxBuilder.TableCell("Span", "gridSpan=\"2\""), PptxBuilder.TableCell("ignored", "hMerge=\"1\""), PptxBuilder.TableCell("Col3")],
            [PptxBuilder.TableCell("", "vMerge=\"1\""), PptxBuilder.TableCell("B2"), PptxBuilder.TableCell("C2")]));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Contains("| A | B | C |\n| --- | --- | --- |\n| Span |  | Col3 |\n|  | B2 | C2 |", markdown);
    }

    [Fact]
    public async Task Reused_picture_is_written_once_and_linked_from_every_slide()
    {
        var deck = new PptxBuilder();
        var logo = deck.Image(await File.ReadAllBytesAsync(EngineFixture.MakeImage(fx.NewFolder(), "logo.png", ImageMagick.MagickFormat.Png), Ct));
        deck.Slide(deck.Title("One") + deck.Picture(logo, 0, 0, 1000000, 1000000));
        deck.Slide(deck.Title("Two") + deck.Picture(logo, 0, 0, 1000000, 1000000));

        var (markdown, output) = await ConvertAsync(deck);

        var files = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(output)!, "deck_files"));
        Assert.Single(files);
        var slides = markdown.Split("\n\n---\n\n");
        Assert.Equal(2, slides.Length);
        Assert.Contains("![Picture](deck_files/img0001.png)", slides[0]);
        Assert.Contains("![Picture](deck_files/img0001.png)", slides[1]);

        var calls = 0;
        var pptx = deck.Save(Path.Combine(fx.NewFolder(), "refused.pptx"));
        SlidesMarkdown.Write(pptx, _ => { calls++; return null; }, fx.NewFolder(), Ct);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Footer_date_and_slide_number_placeholders_are_skipped()
    {
        var deck = new PptxBuilder();
        deck.Slide(
            deck.Title("Topic") +
            deck.Body(("Main point", 0)) +
            deck.Placeholder("ftr", "Confidential") +
            deck.Placeholder("dt", "2026-11-22") +
            deck.Placeholder("sldNum", "1"));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.Equal("## Topic\n\n- Main point\n", markdown);
    }

    [Fact]
    public async Task Empty_first_slide_does_not_start_with_front_matter_separator()
    {
        var deck = new PptxBuilder();
        deck.Slide("");
        deck.Slide(deck.Title("Second"));
        deck.Slide(deck.Title("Third"));

        var (markdown, _) = await ConvertAsync(deck);

        Assert.False(markdown.StartsWith("---", StringComparison.Ordinal));
        var slides = markdown.Split("\n\n---\n\n");
        Assert.Equal(3, slides.Length);
        Assert.Equal("<!-- slide 1 -->", slides[0]);
        Assert.Equal("## Second", slides[1]);
        Assert.Equal("## Third", slides[2].TrimEnd('\n'));
    }
}
