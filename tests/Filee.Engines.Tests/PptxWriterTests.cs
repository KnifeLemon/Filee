// The built-in PPTX writer (document model → slides): Pandoc's slide rules (slide level, section slides, rules,
// pictures and tables on slides of their own), the title slide from the metadata, bullets and numbers with their
// levels, tables with header rows, pictures fitted into the content area, code in a monospace box, speaker notes,
// footnotes, text that runs over continuing on the next slide, every deck valid for the Open XML SDK and read back by
// Filee's own PPTX reader, the routes (it beats Pandoc's slides and never stands between a document and its PDF), and
// hostile input. Every input is built in code.

using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office;
using ImageMagick;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Filee.Engines.Tests;

public class PptxWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>What a slide holds, read with the Open XML SDK.</summary>
    internal sealed record Slide(string Layout, string? Title, List<Paragraph> Body, int Tables, List<P.Picture> Pictures, string Notes, P.Slide Xml);

    /// <summary>A body paragraph: its text, level, bullet ("char", "num:arabicPeriod:3", "none") and fonts.</summary>
    internal sealed record Paragraph(string Text, int Level, string Bullet, int Size, IReadOnlyList<string> Fonts);

    private string Folder() => fx.NewFolder();

    /// <summary>Markdown → model → PPTX with the writer itself.</summary>
    private string Write(string markdown, string? folder = null)
    {
        folder ??= Folder();
        var pptx = Path.Combine(folder, $"deck-{Guid.NewGuid():N}.pptx");
        PptxWriter.Write(MarkdownReader.Read(markdown, folder), pptx);
        return pptx;
    }

    private async Task<string> ConvertAsync(string input, string target = "pptx")
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    internal static List<Slide> Read(string pptx)
    {
        using var document = PresentationDocument.Open(pptx, false);
        var presentation = document.PresentationPart!;
        var slides = new List<Slide>();
        foreach (var id in presentation.Presentation!.SlideIdList!.Elements<P.SlideId>())
        {
            var part = (SlidePart)presentation.GetPartById(id.RelationshipId!.Value!);
            var slide = part.Slide!;
            var shapes = slide.CommonSlideData!.ShapeTree!.Elements<P.Shape>().ToList();
            string? title = null;
            var body = new List<Paragraph>();
            foreach (var shape in shapes)
            {
                var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
                var type = ph?.Type?.Value;
                if (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle)
                {
                    title = string.Concat(shape.TextBody!.Descendants<A.Text>().Select(t => t.Text));
                    continue;
                }
                foreach (var p in shape.TextBody?.Elements<A.Paragraph>() ?? [])
                {
                    var pPr = p.ParagraphProperties;
                    var bullet = pPr?.GetFirstChild<A.CharacterBullet>() is not null ? "char"
                        : pPr?.GetFirstChild<A.AutoNumberedBullet>() is { } number ? $"num:{number.Type!.InnerText}:{number.StartAt?.Value ?? 1}"
                        : "none";
                    var runs = p.Elements<A.Run>().ToList();
                    body.Add(new Paragraph(string.Concat(runs.Select(r => r.Text?.Text)), pPr?.Level?.Value ?? 0, bullet,
                        runs.FirstOrDefault()?.RunProperties?.FontSize?.Value ?? 0,
                        runs.Select(r => r.RunProperties?.GetFirstChild<A.LatinFont>()?.Typeface?.Value ?? "").ToList()));
                }
            }
            slides.Add(new Slide(part.SlideLayoutPart!.SlideLayout!.CommonSlideData!.Name!.Value!, title, body,
                slide.Descendants<A.Table>().Count(), slide.Descendants<P.Picture>().ToList(),
                part.NotesSlidePart?.NotesSlide?.InnerText ?? "", slide));
        }
        return slides;
    }

    /// <summary>
    /// No schema or semantic error for PowerPoint (Open XML SDK), every part has a content type and every relationship
    /// inside the package a target.
    /// </summary>
    internal static void Valid(string pptx)
    {
        using (var document = PresentationDocument.Open(pptx, false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
                .ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var zip = ZipFile.OpenRead(pptx);
        var contentTypes = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open()).Root!;
        var overrides = contentTypes.Elements(types + "Override").Select(o => ((string)o.Attribute("PartName")!).TrimStart('/')).ToHashSet();
        var defaults = contentTypes.Elements(types + "Default").Select(d => (string)d.Attribute("Extension")!).ToHashSet();
        foreach (var entry in zip.Entries.Where(e => e.FullName != "[Content_Types].xml"))
            Assert.True(overrides.Contains(entry.FullName) || defaults.Contains(Path.GetExtension(entry.FullName).TrimStart('.')), $"{entry.FullName} has no content type");
        Assert.All(overrides, part => Assert.NotNull(zip.GetEntry(part)));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = entry.FullName[..entry.FullName.IndexOf("_rels/", StringComparison.Ordinal)];
            foreach (var relationship in XDocument.Load(entry.Open()).Root!.Elements(rels + "Relationship"))
            {
                if ((string?)relationship.Attribute("TargetMode") == "External")
                    continue;
                var target = (string)relationship.Attribute("Target")!;
                var resolved = new Uri(new Uri("pptx:///" + folder), target).AbsolutePath.TrimStart('/');
                Assert.True(zip.GetEntry(resolved) is not null, $"{entry.FullName} → {target}");
            }
        }
    }

    /// <summary>The text Filee's own PPTX reader finds in the deck.</summary>
    private string ReadBack(string pptx)
    {
        var document = PptxReader.Read(pptx, Path.Combine(Folder(), "media"));
        return string.Join("\n", HDocumentWalker.InlineLists(document.Sections.SelectMany(s => s.Blocks))
            .Select(list => string.Concat(list.OfType<HText>().Select(t => t.Text))).Where(t => t.Length > 0));
    }

    private static void Near(long expected, long actual) => Assert.InRange(actual, expected - 2, expected + 2);

    private static string Png(string folder, string name, uint width, uint height)
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    // ───────────────────────── Slides from headings ─────────────────────────

    private const string Talk = """
        ---
        title: Quarterly review
        subtitle: What we shipped
        author:
          - Mina Park
          - Joon Lee
        date: 2026-11-01
        ---

        # Product

        ## Releases

        - Version 2.0 shipped
          - Built-in engines
            - PPTX writer
        - Version 2.1 planned

        ::: notes
        Mention the download numbers.
        :::

        ## Steps

        1. Fetch
        2. Build
        3. Test

        # Engineering

        ## Code

        ```
        dotnet build Filee.slnx
        dotnet test
        ```

        ## Numbers

        | Engine | Files |
        |--------|------:|
        | HWPX   | 1200  |
        | DOCX   | 800   |

        ---

        Closing words.
        """;

    [Fact]
    public void Headings_make_slides_by_pandocs_rules()
    {
        var pptx = Write(Talk);
        Valid(pptx);
        var slides = Read(pptx);

        // H1 is above the slide level (H2 is the highest heading followed by content): section slides.
        Assert.Equal(["Title Slide", "Section Header", "Title and Content", "Title and Content", "Section Header",
            "Title and Content", "Title and Content", "Title and Content"], slides.Select(s => s.Layout));
        Assert.Equal(["Quarterly review", "Product", "Releases", "Steps", "Engineering", "Code", "Numbers", null], slides.Select(s => s.Title));
        // The rule ends the "Numbers" slide: what follows is on an untitled slide.
        Assert.Equal("Closing words.", Assert.Single(slides[^1].Body).Text);

        // Title slide: subtitle and authors under the title, the date in its placeholder.
        var title = slides[0].Body.Select(p => p.Text).ToList();
        Assert.Equal(["What we shipped", "Mina Park", "Joon Lee", "2026-11-01"], title);

        // Bullets with their levels.
        Assert.Equal([("Version 2.0 shipped", 0, "char"), ("Built-in engines", 1, "char"), ("PPTX writer", 2, "char"), ("Version 2.1 planned", 0, "char")],
            slides[2].Body.Select(p => (p.Text, p.Level, p.Bullet)));
        Assert.Equal([2800, 2400, 2000, 2800], slides[2].Body.Select(p => p.Size));
        Assert.Contains("Mention the download numbers.", slides[2].Notes);
        Assert.DoesNotContain(slides[2].Body, p => p.Text.Contains("download", StringComparison.Ordinal));

        // Numbered list: each item carries its own number.
        Assert.Equal(["num:arabicPeriod:1", "num:arabicPeriod:2", "num:arabicPeriod:3"], slides[3].Body.Select(p => p.Bullet));

        // Code in the monospace font, in a filled box, without bullets.
        Assert.Equal(["dotnet build Filee.slnx", "dotnet test"], slides[5].Body.Select(p => p.Text));
        Assert.All(slides[5].Body, p => Assert.Equal([PptxWriter.CodeFont], p.Fonts));
        Assert.All(slides[5].Body, p => Assert.Equal("none", p.Bullet));
        Assert.NotNull(slides[5].Xml.Descendants<P.Shape>().Single(s => s.InnerText.Contains("dotnet test", StringComparison.Ordinal))
            .ShapeProperties!.GetFirstChild<A.SolidFill>());

        // The table: a header row and two rows, three cells each way.
        Assert.Equal(1, slides[6].Tables);
        var table = slides[6].Xml.Descendants<A.Table>().Single();
        Assert.Equal(2, table.TableGrid!.Elements<A.GridColumn>().Count());
        Assert.Equal(["Engine|Files", "HWPX|1200", "DOCX|800"],
            table.Elements<A.TableRow>().Select(r => string.Join("|", r.Elements<A.TableCell>().Select(c => c.InnerText))));
        Assert.True(table.TableProperties!.FirstRow!.Value);
        // The header row's text is white on PowerPoint's blue.
        Assert.Equal("FFFFFF", table.Elements<A.TableRow>().First().Descendants<A.Run>().First().RunProperties!
            .GetFirstChild<A.SolidFill>()!.RgbColorModelHex!.Val!.Value);

        // Filee's own reader reads the deck back.
        var text = ReadBack(pptx);
        foreach (var expected in new[] { "Quarterly review", "Releases", "Built-in engines", "dotnet test", "1200", "Closing words." })
            Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("# A\n\ntext\n\n# B\n\nmore", 1)]
    [InlineData("# A\n\n## B\n\ntext\n\n## C\n\nmore", 2)]
    [InlineData("# A\n\n## B\n\n### C\n\ntext", 3)]
    [InlineData("# A\n## B\n### C", 6)] // no heading is followed by content
    [InlineData("# A\n\n---\n\n## B\n\ntext", 2)] // a rule is no content
    [InlineData("plain text only", 6)]
    public void The_slide_level_is_the_highest_heading_followed_by_content(string markdown, int level)
    {
        var document = MarkdownReader.Read(markdown, Folder());
        Assert.Equal(level, PptxWriter.SlideLevel(document.Sections[0].Blocks));
    }

    [Fact]
    public void Content_before_the_first_heading_and_below_the_slide_level_stays_on_the_slide()
    {
        var slides = Read(Write("Intro text.\n\n## One\n\nFirst.\n\n### Detail\n\nMore.\n\n## Two\n\nSecond."));

        Assert.Equal([null, "One", "Two"], slides.Select(s => s.Title));
        Assert.Equal("Intro text.", Assert.Single(slides[0].Body).Text);
        // A heading below the slide level is a bold paragraph on the slide.
        Assert.Equal(["First.", "Detail", "More."], slides[1].Body.Select(p => p.Text));
        var detail = slides[1].Xml.Descendants<A.Run>().Single(r => r.InnerText == "Detail");
        Assert.True(detail.RunProperties!.Bold!.Value);
    }

    [Fact]
    public void A_picture_joins_a_title_only_slide_or_gets_a_slide_of_its_own_and_fits_the_content_area()
    {
        var folder = Folder();
        Png(folder, "wide.png", 4000, 1000);
        Png(folder, "tall.png", 600, 2400);
        Png(folder, "small.png", 120, 90);
        var pptx = Write("## Chart\n\n![](wide.png)\n\n## Text first\n\nSome words.\n\n![](tall.png)\n\nAfter the picture.\n\n## Icon\n\n![](small.png)\n", folder);
        Valid(pptx);
        var slides = Read(pptx);

        Assert.Equal(["Chart", "Text first", null, null, "Icon"], slides.Select(s => s.Title));
        Assert.Single(slides[0].Pictures);
        Assert.Equal("Some words.", Assert.Single(slides[1].Body).Text);
        Assert.Empty(slides[1].Pictures);
        Assert.Single(slides[2].Pictures); // on an untitled slide of its own
        Assert.Equal("After the picture.", Assert.Single(slides[3].Body).Text);

        foreach (var picture in slides.SelectMany(s => s.Pictures))
        {
            var xfrm = picture.ShapeProperties!.Transform2D!;
            var (x, y, cx, cy) = (xfrm.Offset!.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents!.Cx!.Value, xfrm.Extents.Cy!.Value);
            Assert.InRange(x, PptxWriter.BodyX, PptxWriter.BodyX + PptxWriter.BodyWidth);
            Assert.InRange(y, PptxWriter.BodyY, PptxWriter.BodyY + PptxWriter.BodyHeight);
            Assert.True(x + cx <= PptxWriter.BodyX + PptxWriter.BodyWidth + 1 && y + cy <= PptxWriter.BodyY + PptxWriter.BodyHeight + 1, $"{x},{y} {cx}×{cy}");
        }
        // The wide picture spans the content width, the tall one its height, both keeping their shape.
        var wide = slides[0].Pictures[0].ShapeProperties!.Transform2D!.Extents!;
        Near(PptxWriter.BodyWidth, wide.Cx!.Value);
        Assert.Equal(4.0, (double)wide.Cx.Value / wide.Cy!.Value, 2);
        var tall = slides[2].Pictures[0].ShapeProperties!.Transform2D!.Extents!;
        Near(PptxWriter.BodyHeight, tall.Cy!.Value);
        // A small picture is not enlarged (96 dpi: 120 px = 90 pt) and stays centred.
        var small = slides[4].Pictures[0].ShapeProperties!.Transform2D!;
        Near(90 * 12700, small.Extents!.Cx!.Value);
        Near(PptxWriter.BodyX + (PptxWriter.BodyWidth - small.Extents.Cx.Value) / 2, small.Offset!.X!.Value);
    }

    // ───────────────────────── Text that runs over ─────────────────────────

    [Fact]
    public void Text_that_does_not_fit_continues_on_slides_with_the_same_title()
    {
        var items = Enumerable.Range(1, 40).Select(i => $"- Item number {i} of a long list").ToList();
        var long1 = string.Join(" ", Enumerable.Repeat("This sentence makes one paragraph far longer than a slide can hold.", 60));
        var pptx = Write("## Long list\n\n" + string.Join("\n", items) + "\n\n## Long paragraph\n\n" + long1 + "\n");
        Valid(pptx);
        var slides = Read(pptx);

        var list = slides.Where(s => s.Title == "Long list").ToList();
        Assert.True(list.Count >= 3, $"{list.Count} slides");
        // Nothing is lost or repeated, and every item keeps its bullet.
        Assert.Equal(items.Select(i => i[2..]), list.SelectMany(s => s.Body).Select(p => p.Text));
        Assert.All(list.SelectMany(s => s.Body), p => Assert.Equal("char", p.Bullet));
        // Each continuation fits: no more than the area takes at 28 pt (1.2 lines, 10 pt before).
        Assert.All(list, s => Assert.InRange(s.Body.Count, 5, 10));

        var paragraph = slides.Where(s => s.Title == "Long paragraph").ToList();
        Assert.True(paragraph.Count >= 2);
        var joined = string.Join(" ", paragraph.SelectMany(s => s.Body).Select(p => p.Text));
        Assert.Equal(long1, joined);
    }

    [Fact]
    public void A_long_table_continues_with_its_header_row_and_long_code_by_lines()
    {
        var rows = Enumerable.Range(1, 30).Select(i => $"| row {i} | {i * 7} |");
        var code = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}();"));
        var pptx = Write("## Table\n\n| Name | Value |\n|---|---|\n" + string.Join("\n", rows) + "\n\n## Code\n\n```\n" + code + "\n```\n");
        Valid(pptx);
        var slides = Read(pptx);

        var tables = slides.Where(s => s.Title == "Table").Select(s => s.Xml.Descendants<A.Table>().Single()).ToList();
        Assert.True(tables.Count >= 2);
        Assert.All(tables, t => Assert.Equal("NameValue", t.Elements<A.TableRow>().First().InnerText));
        var body = tables.SelectMany(t => t.Elements<A.TableRow>().Skip(1)).Select(r => r.Elements<A.TableCell>().First().InnerText).ToList();
        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"row {i}"), body);

        var codeSlides = slides.Where(s => s.Title == "Code").ToList();
        Assert.True(codeSlides.Count >= 2);
        Assert.Equal(code.Split('\n'), codeSlides.SelectMany(s => s.Body).Select(p => p.Text));
    }

    [Fact]
    public void A_long_title_shrinks_to_its_box()
    {
        var title = string.Join(" ", Enumerable.Repeat("A very long slide title", 8));
        var slide = Read(Write($"## {title}\n\ntext"))[0];
        var autofit = slide.Xml.Descendants<P.Shape>().First().TextBody!.BodyProperties!.GetFirstChild<A.NormalAutoFit>()!;
        Assert.InRange(autofit.FontScale!.Value, 40000, 90000);
        Assert.Equal(title, slide.Title);
    }

    // ───────────────────────── Notes, footnotes, links ─────────────────────────

    [Fact]
    public void Footnotes_go_on_a_notes_slide_and_links_stay_links()
    {
        var pptx = Write("## Facts\n\nFilee is free[^1] and open[^2]. See [the site](https://filee.sh/docs?a=1&b=2) or [a bookmark](#facts).\n\n[^1]: MIT licence.\n[^2]: On GitHub.\n");
        Valid(pptx);
        var slides = Read(pptx);

        Assert.Equal(["Facts", PptxWriter.EndNotesTitle], slides.Select(s => s.Title));
        Assert.Equal("Filee is free1 and open2. See the site or a bookmark.", slides[0].Body[0].Text);
        Assert.Equal([("MIT licence.", "num:arabicPeriod:1"), ("On GitHub.", "num:arabicPeriod:2")], slides[1].Body.Select(p => (p.Text, p.Bullet)));

        using var document = PresentationDocument.Open(pptx, false);
        var part = document.PresentationPart!.SlideParts.First(p => p.Slide!.InnerText.Contains("Facts", StringComparison.Ordinal));
        var link = Assert.Single(part.HyperlinkRelationships);
        Assert.Equal("https://filee.sh/docs?a=1&b=2", link.Uri.OriginalString);
    }

    [Fact]
    public void A_deck_without_headings_or_metadata_is_untitled_slides()
    {
        var slides = Read(Write("First paragraph.\n\nSecond paragraph."));
        Assert.Equal("Title and Content", Assert.Single(slides).Layout);
        Assert.Null(slides[0].Title);
        Assert.Equal(["First paragraph.", "Second paragraph."], slides[0].Body.Select(p => p.Text));

        var empty = Read(Write(""));
        Assert.Single(empty); // a presentation needs a slide
    }

    [Fact]
    public void Korean_text_wraps_by_characters_and_continues()
    {
        var text = string.Concat(Enumerable.Repeat("한글 문서를 슬라이드로 바꾸면 긴 문단은 다음 슬라이드로 이어집니다. ", 40)).Trim();
        var slides = Read(Write("## 보고서\n\n" + text));
        Assert.True(slides.Count >= 2);
        Assert.All(slides, s => Assert.Equal("보고서", s.Title));
        Assert.Equal(text.Replace(" ", ""), string.Concat(slides.SelectMany(s => s.Body).Select(p => p.Text)).Replace(" ", ""));
    }

    // ───────────────────────── Other readers ─────────────────────────

    [Fact]
    public async Task Every_built_in_reader_reaches_pptx_through_the_writer()
    {
        var folder = Folder();
        var md = Path.Combine(folder, "talk.md");
        await File.WriteAllTextAsync(md, Talk, TestContext.Current.CancellationToken);
        var html = Path.Combine(folder, "page.html");
        await File.WriteAllTextAsync(html, "<html><head><title>Page</title></head><body><h2>Intro</h2><p>Hello <b>web</b>.</p><pre>let x = 1;</pre><hr><h2>Table</h2><table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table></body></html>",
            TestContext.Current.CancellationToken);
        var txt = Path.Combine(folder, "notes.txt");
        await File.WriteAllTextAsync(txt, "line one\nline two\n", TestContext.Current.CancellationToken);
        var csv = Path.Combine(folder, "data.csv");
        await File.WriteAllTextAsync(csv, "name,value\nalpha,1\nbeta,2\n", TestContext.Current.CancellationToken);
        var docx = Path.Combine(folder, "report.docx");
        DocxWriter.Write(MarkdownReader.Read("# Report\n\nWord text.\n\n- point", folder), docx);

        foreach (var (input, expected) in new[] { (md, "Built-in engines"), (html, "let x = 1;"), (txt, "line two"), (csv, "alpha"), (docx, "Word text.") })
        {
            var pptx = await ConvertAsync(input);
            Valid(pptx);
            Assert.Contains(expected, ReadBack(pptx));
        }

        // HTML: <pre> is code, <hr> ends the slide.
        var page = Read(await ConvertAsync(html));
        Assert.Equal(["Page", "Intro", "Table"], page.Select(s => s.Title));
        Assert.Equal([PptxWriter.CodeFont], page[1].Body.Single(p => p.Text == "let x = 1;").Fonts);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void The_writer_beats_pandocs_slides_and_never_stands_between_a_document_and_its_pdf()
    {
        var everything = fx.Catalog.CreatePlanner(fx.Converters.Select(c => c.Id).ToArray());
        foreach (var from in new[] { "md", "docx", "odt", "html", "txt", "xlsx", "csv", "hwpx", "epub" })
            Assert.Equal("pptx-writer", Assert.Single(everything.Plan(from, "pptx")!.Steps).Converter.Id);
        Assert.Equal("pdf-slides", Assert.Single(everything.Plan("pdf", "pptx")!.Steps).Converter.Id);
        Assert.Equal("ooxml", Assert.Single(everything.Plan("pptm", "pptx")!.Steps).Converter.Id);

        // With Pandoc first in the engine order the writer still wins.
        var pandocFirst = new ConverterCatalog(fx.Converters) { Priority = ["pandoc", .. fx.Catalog.Priority.Where(p => p != "pandoc")] }
            .CreatePlanner(fx.Converters.Select(c => c.Id).ToArray());
        Assert.Equal("pptx-writer", Assert.Single(pandocFirst.Plan("md", "pptx")!.Steps).Converter.Id);

        // Without Pandoc, Markdown reaches the other presentation formats through the writer and LibreOffice.
        var noPandoc = new ConverterCatalog(fx.Converters.Where(c => c.Id != "pandoc")) { Priority = fx.Catalog.Priority }
            .CreatePlanner(["libreoffice"]);
        Assert.Equal(["pptx-writer", "libreoffice"], noPandoc.Plan("md", "odp")!.Steps.Select(s => s.Converter.Id));

        // Every other route is the one it was without the writer, except routes to other presentation formats that
        // had none (or went through Pandoc's slides).
        var all = fx.Converters.Select(c => c.Id).ToArray();
        var with = fx.Catalog.CreatePlanner(all);
        var without = new ConverterCatalog(fx.Converters.Where(c => c is not PptxWriterConverter)) { Priority = fx.Catalog.Priority }.CreatePlanner(all);
        var formats = FormatRegistry.Known.Select(f => f.Id).ToList();
        var checkedRoutes = 0;
        foreach (var from in formats)
        {
            foreach (var to in formats.Where(t => t != from && t != "pptx"))
            {
                var route = with.Plan(from, to);
                var before = without.Plan(from, to);
                if (route?.Steps.Any(s => s.Converter is PptxWriterConverter) == true)
                {
                    Assert.True(FormatRegistry.Get(to).Category == FormatCategory.Presentation, $"{from} → {to} goes through the PPTX writer");
                    Assert.True(before is null || before.Steps.Any(s => s.To == "pptx" && s.Converter.Id == "pandoc"), $"{from} → {to}");
                    continue;
                }
                // Equal costs: where two engines tie (the DOCX and ODT writers on a long route) either may be planned.
                Assert.Equal(before?.Cost, route?.Cost);
                checkedRoutes++;
            }
        }
        Assert.True(checkedRoutes > 1000);

        // Presentations → PDF with LibreOffice chosen (PPTX → PDF costs nothing): Markdown still goes the document way.
        Assert.DoesNotContain(with.Plan("md", "pdf")!.Steps, s => s.To == "pptx");
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void Hostile_documents_still_give_valid_decks_or_are_refused()
    {
        var folder = Folder();

        // Control characters, a broken link, a missing picture, a damaged table and tables nested in cells.
        var document = new HDocument { Title = "Bad\u0001title￿" };
        var section = new HSection();
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("text\u0002 with\u0000 controls", default));
        var link = new HLink("http://exa mple.invalid/\u0001");
        link.Content.Add(new HText("broken link", default));
        paragraph.Inlines.Add(link);
        paragraph.Inlines.Add(new HImage(Path.Combine(folder, "missing.png")));
        section.Blocks.Add(paragraph);
        var table = new HTable { ColumnCount = 100_000 };
        var row = new HRow();
        row.Cells.Add(new HCell { Column = 5, ColSpan = int.MaxValue, RowSpan = 1_000_000 });
        var nested = new HTable { ColumnCount = 1 };
        HTable inner = nested;
        for (var depth = 0; depth < 30; depth++)
        {
            var innerRow = new HRow();
            var cell = new HCell();
            var child = new HTable { ColumnCount = 1 };
            var label = new HParagraph();
            label.Inlines.Add(new HText($"depth {depth}", default));
            cell.Blocks.Add(label);
            cell.Blocks.Add(child);
            innerRow.Cells.Add(cell);
            inner.Rows.Add(innerRow);
            inner = child;
        }
        table.Rows.Add(row);
        section.Blocks.Add(table);
        var holder = new HTable { ColumnCount = 1 };
        var holderRow = new HRow();
        var outer = new HCell();
        outer.Blocks.Add(nested);
        holderRow.Cells.Add(outer);
        holder.Rows.Add(holderRow);
        section.Blocks.Add(holder);
        document.Sections.Add(section);
        var pptx = Path.Combine(folder, "hostile.pptx");
        PptxWriter.Write(document, pptx);
        Valid(pptx);
        Assert.Contains("depth 29", string.Concat(Read(pptx).Select(s => s.Xml.InnerText)));

        // More slides than a document may become: refused, and no file is left behind.
        var many = new HDocument();
        var blocks = new HSection();
        for (var i = 0; i <= DocumentLimits.Default.MaxPages; i++)
        {
            var heading = new HParagraph { HeadingLevel = 2 };
            heading.Inlines.Add(new HText("Slide", default));
            blocks.Blocks.Add(heading);
            var text = new HParagraph();
            text.Inlines.Add(new HText("x", default));
            blocks.Blocks.Add(text);
        }
        many.Sections.Add(blocks);
        var refused = Path.Combine(folder, "many.pptx");
        var error = Assert.Throws<ConversionRefusedException>(() => PptxWriter.Write(many, refused));
        Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
        Assert.False(File.Exists(refused));
    }

    [Fact]
    public void Front_matter_that_is_hostile_or_broken_still_gives_its_title()
    {
        var aliases = "---\ntitle: Real\na: &a [x, x, x]\nb: [*a, *a, *a]\nauthor: *a\nsubtitle: [[[[[[[[[[deep]]]]]]]]]]\n---\n\ntext";
        var document = MarkdownReader.Read(aliases, Folder());
        Assert.Equal("Real", document.Title);
        Assert.Null(document.Subtitle);
        Assert.Empty(document.Authors);

        var broken = MarkdownReader.Read("---\ntitle: Broken\nkey: [unclosed\n---\n\ntext", Folder());
        Assert.Equal("Broken", broken.Title);
    }
}
