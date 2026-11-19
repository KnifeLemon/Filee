// PDF → PPTX with editable slides (#1150): text boxes with the PDF's text, fonts, sizes, colours, bold / italic and
// links where the PDF has them; pictures as picture shapes; rectangles and lines as shapes, a page-filling rectangle as
// the background, other drawings as one picture; ruled tables as tables. The PDFs are built in code (PdfPig's writer
// with the standard 14 fonts, PDFsharp for a curve and for passwords). Positions are compared with a few points of
// slack and text by content, so other fonts on Linux don't matter. Filee's own slide reader and renderer read the
// result back; hostile pages (thousands of shapes, a huge drawing) finish quickly and fuzzed PDFs are read or refused.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Slides;
using ImageMagick;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class PdfEditableSlidesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const double Width = 960, Height = 540;
    private const double Emu = 12700;

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A 16:9 page like an exported slide: a dark header bar with a white bold title, a wrapped paragraph, a red italic
    /// note with a link, a picture, a rule, a ruled 3 × 3 table and a green curve (PDFsharp).
    /// </summary>
    private static string MakeDeckPdf(string folder)
    {
        var pdf = new PdfBuilder().NewPage(Width, Height);
        pdf.Bar(0, 0, Width, 60, (0x1F, 0x4E, 0x79));
        pdf.Line("Quarterly results", 28, pdf.Font(bold: true), 40, 42, (255, 255, 255));
        pdf.Paragraph("Sales grew in every region this quarter, led by the new stores in the north and a strong season online. " +
                      "Costs stayed flat, so the margin is the best in three years.", 14, pdf.Font(), 40, 110, 420, 18);
        pdf.Line("Details at filee.sh", 12, pdf.Font(italic: true), 40, 250, (255, 0, 0));
        pdf.Link("https://filee.sh/", 40, 238, 120, 16);
        pdf.Picture(PdfBuilder.Photo(folder, "photo.png", MagickFormat.Png, 190, 100), 520, 90, 380, 200);
        pdf.Rule(40, 290, 480, 290, 1);
        // A ruled table: 3 columns of 120 pt, 3 rows of 30 pt, from (40, 320).
        for (var r = 0; r <= 3; r++)
            pdf.Rule(40, 320 + 30 * r, 400, 320 + 30 * r);
        for (var c = 0; c <= 3; c++)
            pdf.Rule(40 + 120 * c, 320, 40 + 120 * c, 410);
        string[][] cells = [["Region", "Q1", "Q2"], ["North", "120", "150"], ["South", "90", "95"]];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                pdf.Line(cells[r][c], 11, r == 0 ? pdf.Font(bold: true) : pdf.Font(), 46 + 120 * c, 340 + 30 * r);
        }
        var path = pdf.Save(Path.Combine(folder, "deck.pdf"));
        // A filled curve, in PDF coordinates (y up): a green hill at the bottom right.
        AppendContent(path, "q 0 0.6 0 rg 600 60 m 650 200 800 200 900 60 c h f Q");
        return path;
    }

    private static void AppendContent(string path, string content)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        document.Pages[0].Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\n" + content + "\n"));
        document.Save(path);
    }

    private async Task<ConversionJob> RunAsync(string input, Preset? preset = null) =>
        await fx.ConvertAsync([input], preset ?? new Preset { TargetFormat = "pptx" });

    private async Task<string> ConvertAsync(string input, Preset? preset = null)
    {
        var job = await RunAsync(input, preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>The shape trees of the slides and each slide's relationships (id → target).</summary>
    private static List<(XElement Tree, Dictionary<string, string> Relationships)> Slides(string pptx)
    {
        using var zip = ZipFile.OpenRead(pptx);
        var result = new List<(XElement, Dictionary<string, string>)>();
        for (var n = 1; zip.GetEntry($"ppt/slides/slide{n}.xml") is { } entry; n++)
        {
            var tree = XDocument.Load(entry.Open()).Root!.Element(P + "cSld")!;
            var rels = XDocument.Load(zip.GetEntry($"ppt/slides/_rels/slide{n}.xml.rels")!.Open()).Root!.Elements()
                .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
            result.Add((tree, rels));
        }
        return result;
    }

    private static string Text(XElement shape) => string.Concat(shape.Descendants(A + "t").Select(t => t.Value));

    /// <summary>A shape's box in points.</summary>
    private static (double X, double Y, double Width, double Height) Box(XElement shape)
    {
        var xfrm = shape.Descendants().First(e => e.Name.LocalName == "xfrm");
        var off = xfrm.Element(A + "off")!;
        var ext = xfrm.Element(A + "ext")!;
        return ((long)off.Attribute("x")! / Emu, (long)off.Attribute("y")! / Emu, (long)ext.Attribute("cx")! / Emu, (long)ext.Attribute("cy")! / Emu);
    }

    private static XElement TextBox(XElement tree, string contains) =>
        tree.Descendants(P + "sp").Single(s => s.Element(P + "txBody") is not null && Text(s).Contains(contains, StringComparison.Ordinal));

    [Fact]
    public async Task Text_becomes_text_boxes_with_the_pdfs_fonts_sizes_and_colours()
    {
        var pptx = await ConvertAsync(MakeDeckPdf(fx.NewFolder()));

        PdfSlidesTests.ValidPackage(pptx);
        var (tree, rels) = Assert.Single(Slides(pptx));

        // The title: one bold white run of 28 pt, its box where the PDF has it (baseline 42 pt down, 40 pt in).
        var title = TextBox(tree, "Quarterly results");
        var run = title.Descendants(A + "rPr").First();
        Assert.Equal("2800", (string?)run.Attribute("sz"));
        Assert.Equal("1", (string?)run.Attribute("b"));
        Assert.Equal("FFFFFF", (string?)run.Descendants(A + "srgbClr").Single().Attribute("val"));
        Assert.Equal("Arial", (string?)run.Element(A + "latin")?.Attribute("typeface")); // Helvetica → Arial
        var (x, y, _, _) = Box(title);
        Assert.InRange(x, 38, 42);
        Assert.InRange(y, 42 - 28 - 4, 42 - 28 + 4);
        Assert.Equal("none", (string?)title.Descendants(A + "bodyPr").Single().Attribute("wrap"));

        // The wrapped paragraph is one paragraph of a box that wraps, as wide as the PDF's lines and a little more.
        var paragraph = TextBox(tree, "Sales grew");
        Assert.Single(paragraph.Descendants(A + "p"));
        Assert.Contains("led by the new stores in the north and a strong season online. Costs stayed flat, so the margin is the best in three years.", Text(paragraph));
        Assert.Empty(paragraph.Descendants(A + "br")); // the lines wrap by themselves
        Assert.Equal("square", (string?)paragraph.Descendants(A + "bodyPr").Single().Attribute("wrap"));
        var box = Box(paragraph);
        Assert.InRange(box.X, 38, 42);
        Assert.InRange(box.Width, 380, 450);
        Assert.InRange(box.Y, 110 - 14 - 4, 110 - 14 + 4);

        // The note: red italic, its words a link to the PDF's URL.
        var note = TextBox(tree, "Details at filee.sh");
        var noteRun = note.Descendants(A + "rPr").First();
        Assert.Equal("1", (string?)noteRun.Attribute("i"));
        Assert.Equal("FF0000", (string?)noteRun.Descendants(A + "srgbClr").Single().Attribute("val"));
        var link = (string?)note.Descendants(A + "hlinkClick").First().Attribute(R + "id");
        Assert.Equal("https://filee.sh/", rels[link!]);

        // The table's text is in the table, not in text boxes.
        Assert.DoesNotContain(tree.Descendants(P + "sp"), s => Text(s).Contains("North", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pictures_shapes_and_tables_keep_their_places()
    {
        var pptx = await ConvertAsync(MakeDeckPdf(fx.NewFolder()));
        var (tree, rels) = Assert.Single(Slides(pptx));

        // The header bar is a rectangle with the PDF's fill, the rule a line.
        var bar = tree.Descendants(P + "sp").Single(s => s.Element(P + "txBody") is null);
        Assert.Equal("1F4E79", (string?)bar.Element(P + "spPr")!.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val"));
        var (bx, by, bw, bh) = Box(bar);
        Assert.Equal((0, 0, Width, 60), (Math.Round(bx), Math.Round(by), Math.Round(bw), Math.Round(bh)));
        var rule = Assert.Single(tree.Descendants(P + "cxnSp"));
        var (rx, ry, rw, rh) = Box(rule);
        Assert.Equal((40, 290, 440, 0), (Math.Round(rx), Math.Round(ry), Math.Round(rw), Math.Round(rh)));

        // Two pictures: the photo where it was, and the drawing of the curve over the part of the page it covers.
        var pictures = tree.Descendants(P + "pic").ToList();
        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, p => Assert.StartsWith("../media/", rels[(string)p.Descendants(A + "blip").Single().Attribute(R + "embed")!]));
        var photo = pictures.Select(Box).Single(b => Math.Abs(b.X - 520) < 1);
        Assert.Equal((90, 380, 200), (Math.Round(photo.Y), Math.Round(photo.Width), Math.Round(photo.Height)));
        var curve = pictures.Select(Box).Single(b => Math.Abs(b.X - 520) >= 1);
        Assert.InRange(curve.X, 598, 601);
        Assert.InRange(curve.X + curve.Width, 899, 902);
        Assert.InRange(curve.Y + curve.Height, Height - 61, Height - 59);

        // The ruled table: 3 × 3 cells of the PDF's sizes, with its text and its rules as cell borders.
        var table = tree.Descendants(A + "tbl").Single();
        Assert.Equal([120, 120, 120], table.Element(A + "tblGrid")!.Elements(A + "gridCol").Select(c => Math.Round((long)c.Attribute("w")! / Emu)));
        var rows = table.Elements(A + "tr").ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(30, Math.Round((long)r.Attribute("h")! / Emu)));
        Assert.Equal(["Region", "Q1", "Q2", "North", "120", "150", "South", "90", "95"], rows.SelectMany(r => r.Elements(A + "tc")).Select(Text));
        Assert.All(table.Descendants(A + "lnT"), l => Assert.NotNull(l.Element(A + "solidFill")));
        var frame = Box(table.Ancestors(P + "graphicFrame").Single());
        Assert.Equal((40, 320), (Math.Round(frame.X), Math.Round(frame.Y)));
        // The table's rules are its borders: no line shapes for them.
        Assert.DoesNotContain(tree.Descendants(P + "cxnSp"), l => Box(l).Y > 310);

        // Back to front: the bar and pictures, then the table, then the text.
        var order = tree.Elements(P + "spTree").Single().Elements().Select(e => e.Name.LocalName).Where(n => n is not ("nvGrpSpPr" or "grpSpPr")).ToList();
        Assert.True(order.IndexOf("graphicFrame") > order.LastIndexOf("pic"));
        Assert.True(order.FindIndex(n => n == "sp") < order.IndexOf("graphicFrame"));
    }

    [Fact]
    public async Task Filees_slide_reader_and_renderer_show_the_rebuilt_slide()
    {
        var pptx = await ConvertAsync(MakeDeckPdf(fx.NewFolder()));

        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        Assert.Equal(((float)Width, (float)Height), (deck.Width, deck.Height));
        var text = string.Concat(deck.Slides[0].Elements.OfType<SlideShape>().Select(s => s.Text).OfType<SlideTextFrame>()
            .SelectMany(f => f.Paragraphs).SelectMany(p => p.Inlines).OfType<Hwp.Hwpx.HText>().Select(t => t.Text));
        Assert.Contains("Quarterly results", text);
        Assert.Contains("Sales grew", text);

        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        // Header bar, the curve's fill, white page.
        Assert.Equal(new SKColor(0x1F, 0x4E, 0x79), bitmap.GetPixel(900, 10));
        var green = bitmap.GetPixel(750, (int)Height - 80);
        Assert.True(green.Green > 120 && green.Red < 40 && green.Blue < 40, green.ToString());
        Assert.Equal(SKColors.White, bitmap.GetPixel(700, 300));
    }

    [Fact]
    public async Task A_page_filling_rectangle_is_the_background_and_pages_of_another_size_are_fitted()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage(Width, Height);
        pdf.Bar(0, 0, Width, Height, (0x22, 0x22, 0x22));
        pdf.Line("Dark slide", 40, pdf.Font(bold: true), 100, 200, (255, 255, 255));
        pdf.NewPage(Height, Width); // a portrait page after a landscape one
        pdf.Line("Portrait", 20, pdf.Font(), 50, 100);
        var pptx = await ConvertAsync(pdf.Save(Path.Combine(folder, "dark.pdf")));

        PdfSlidesTests.ValidPackage(pptx);
        var slides = Slides(pptx);
        Assert.Equal(2, slides.Count);
        Assert.Equal("222222", (string?)slides[0].Tree.Element(P + "bg")!.Descendants(A + "srgbClr").Single().Attribute("val"));
        Assert.DoesNotContain(slides[0].Tree.Descendants(P + "sp"), s => s.Element(P + "txBody") is null);
        // The portrait page is scaled to the slide's height (540 / 960) and centred: its text shrinks with it.
        var portrait = TextBox(slides[1].Tree, "Portrait");
        var scale = Height / Width;
        Assert.Equal(((int)Math.Round(2000 * scale)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)portrait.Descendants(A + "rPr").First().Attribute("sz"));
        var (x, _, _, _) = Box(portrait);
        Assert.InRange(x, (Width - Height * scale) / 2 + 50 * scale - 2, (Width - Height * scale) / 2 + 50 * scale + 2);
    }

    [Fact]
    public async Task Pages_without_text_keep_their_pictures_and_the_page_range_picks_the_slides()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder();
        for (var i = 1; i <= 3; i++)
        {
            pdf.NewPage(Width, Height);
            pdf.Picture(PdfBuilder.Photo(folder, $"scan{i}.jpg", MagickFormat.Jpeg, 320, 180), 0, 0, Width, Height);
        }
        var preset = new Preset { TargetFormat = "pptx", Pdf = { PageRange = "2-3" } };
        var pptx = await ConvertAsync(pdf.Save(Path.Combine(folder, "scans.pdf")), preset);

        var slides = Slides(pptx);
        Assert.Equal(["Page 2", "Page 3"], slides.Select(s => (string)s.Tree.Attribute("name")!));
        // A picture filling the page (a scan, a background) is kept as it is, a JPEG.
        Assert.All(slides, s =>
        {
            var picture = Assert.Single(s.Tree.Descendants(P + "pic"));
            Assert.Equal((0, 0, Width, Height), Box(picture));
            Assert.EndsWith(".jpeg", s.Relationships[(string)picture.Descendants(A + "blip").Single().Attribute(R + "embed")!]);
        });
    }

    [Fact]
    public async Task Slides_as_pictures_is_still_there_as_an_option()
    {
        var pptx = await ConvertAsync(MakeDeckPdf(fx.NewFolder()), new Preset { TargetFormat = "pptx", Pdf = { SlidePictures = true } });

        var (tree, _) = Assert.Single(Slides(pptx));
        Assert.Single(tree.Descendants(P + "pic"));
        Assert.Empty(tree.Descendants(P + "sp"));
    }

    [Fact]
    public async Task Password_protected_and_broken_pdfs_fail_with_the_usual_messages()
    {
        var folder = fx.NewFolder();
        var path = MakeDeckPdf(folder);
        PdfBuilder.Protect(path, "1234");
        var job = await RunAsync(path);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);

        var broken = Path.Combine(folder, "broken.pdf");
        await File.WriteAllTextAsync(broken, "%PDF-1.7\nthis is not really a PDF\n", Token);
        Assert.Equal(JobState.Failed, (await RunAsync(broken)).State);
        Assert.Empty(Directory.GetFiles(folder, "*.pptx*"));
    }

    // ───────────────────────── Hostile pages ─────────────────────────

    [Fact]
    public async Task Thousands_of_shapes_and_a_huge_drawing_become_one_picture_quickly()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage(Width, Height);
        var path = pdf.Line("Chart", 20, pdf.Font(), 40, 40).Save(Path.Combine(folder, "chart.pdf"));
        var content = new StringBuilder("q 0 0 1 rg ");
        // 3,000 small bars (more than MaxShapes) and one path of 600,000 segments (more than MaxGraphicsSegments).
        for (var i = 0; i < 3000; i++)
            content.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i % 100 * 9} {i / 100 * 9} 6 6 re ");
        content.Append("f 1 0 0 RG 0 0 m ");
        for (var i = 0; i < 600_000; i++)
            content.Append(i % 2 == 0 ? "960 270 l " : "0 270 l ");
        content.Append("S Q");
        AppendContent(path, content.ToString());

        var stopwatch = Stopwatch.StartNew();
        var pptx = await ConvertAsync(path);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());

        var (tree, _) = Assert.Single(Slides(pptx));
        Assert.DoesNotContain(tree.Descendants(P + "sp"), s => s.Element(P + "txBody") is null);
        Assert.Single(tree.Descendants(P + "pic"));
        Assert.Contains("Chart", Text(tree));
    }

    /// <summary>
    /// Mutated PDFs (PdfStreamFuzzer) are read into slides or refused. PdfPig's own exceptions for a broken file count
    /// as refusals (the conversion fails with them, as PDF → DOCX does); anything thrown in Filee's code is a failure.
    /// </summary>
    [Fact]
    [Trait("Category", "Fuzz")]
    public void Slide_reading_reads_or_refuses_every_mutated_pdf()
    {
        var folder = fx.NewFolder();
        var seed = File.ReadAllBytes(MakeDeckPdf(folder));
        new FuzzHarness(fx, 40, 20261120).Files("pdf-slides", "pdf", [seed], (bytes, random) => new PdfStreamFuzzer(bytes, random).Next, (path, media) =>
        {
            try
            {
                PdfDocumentReader.ReadSlides(path, count => [.. Enumerable.Range(0, Math.Min(count, 2))], media, 72,
                    page => PdfSlideXml.Shapes(page, new PdfSlideXml.Placement(0, 0, Emu), _ => "rId2", _ => "rId3"));
            }
            catch (Exception ex) when (ThrownByPdfPig(ex) || ex is InvalidOperationException { Message: var message } && message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PdfPig can't read the PDF.", ex);
            }
        });
    }

    /// <summary>The innermost frame of <paramref name="exception"/> outside the runtime's own code is PdfPig's.</summary>
    private static bool ThrownByPdfPig(Exception exception) =>
        new StackTrace(exception).GetFrames().Select(f => f.GetMethod()?.DeclaringType?.Namespace ?? "")
            .FirstOrDefault(n => !n.StartsWith("System", StringComparison.Ordinal))?.StartsWith("UglyToad.", StringComparison.Ordinal) == true;
}
