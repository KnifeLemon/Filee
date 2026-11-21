// PDF → PPTX with editable slides (#1150): text boxes with the PDF's text, fonts, sizes, colours, bold / italic and
// links where the PDF has them; pictures as picture shapes; rectangles and lines as shapes, a page-filling rectangle as
// the background, other drawings as one picture; ruled tables as tables. The PDFs are built in code (PdfPig's writer
// with the standard 14 fonts, PDFsharp for a curve and for passwords). Positions are compared with a few points of
// slack and text by content, so other fonts on Linux don't matter. Filee's own slide reader and renderer read the
// result back; hostile pages (thousands of shapes, a huge drawing) finish quickly and fuzzed PDFs are read or refused.
// Pages written as raw content streams (PDFsharp's objects) check what the paint order recorder reads: turned text,
// words far apart, shadings, transparency, soft masks and tiling patterns, turned, mirrored, clipped and stencil
// pictures, the paint order, and comments as speaker notes.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Slides;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Sharp = PdfSharp.Pdf;

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

        // Back to front as the PDF paints them: the bar, the title on it, ..., the photo, the table, and the curve the
        // PDF draws last.
        var shapes = tree.Elements(P + "spTree").Single().Elements().Where(e => e.Name.LocalName is not ("nvGrpSpPr" or "grpSpPr")).ToList();
        var order = shapes.Select(e => e.Name.LocalName).ToList();
        Assert.Equal(0, shapes.IndexOf(bar));
        Assert.True(shapes.IndexOf(TextBox(tree, "Quarterly results")) > shapes.IndexOf(bar));
        Assert.True(order.IndexOf("graphicFrame") > order.IndexOf("pic"));
        Assert.Equal("pic", order[^1]);
        Assert.InRange(Box(shapes[^1]).X, 598, 601);
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
        // Function-based and mesh shadings, drawn soft masks, a picture under a gradient mask and blend modes.
        var painting = File.ReadAllBytes(MakePaintingPdf(folder));
        new FuzzHarness(fx, 40, 20261120).Files("pdf-slides", "pdf", [seed, painting], (bytes, random) => new PdfStreamFuzzer(bytes, random).Next, (path, media) =>
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

    // ───────────────────────── PDFs written as raw content ─────────────────────────

    /// <summary>
    /// A one-page PDF of <see cref="Width"/> × <see cref="Height"/> points (PDFsharp's low-level objects) with
    /// <paramref name="content"/> as its content stream, Helvetica as /F1 and what <paramref name="resources"/> adds.
    /// </summary>
    private static string RawPdf(string path, string content, Action<Sharp.PdfDocument, Sharp.PdfPage>? resources = null)
    {
        using var document = new Sharp.PdfDocument();
        var page = document.AddPage();
        page.Elements.SetRectangle("/MediaBox", new Sharp.PdfRectangle(new XPoint(0, 0), new XPoint(Width, Height)));
        var font = new Sharp.PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        Resource(document, page, "/Font", "/F1", font);
        resources?.Invoke(document, page);
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(content));
        document.Save(path);
        return path;
    }

    /// <summary>Adds <paramref name="value"/> (made an indirect object) to the page's resources of <paramref name="kind"/>.</summary>
    private static void Resource(Sharp.PdfDocument document, Sharp.PdfPage page, string kind, string name, Sharp.PdfDictionary value)
    {
        if (!value.IsIndirect)
            document.Internals.AddObject(value);
        if (page.Resources.Elements.GetDictionary(kind) is not { } group)
            page.Resources.Elements[kind] = group = new Sharp.PdfDictionary(document);
        group.Elements.SetReference(name, value);
    }

    private static Sharp.PdfArray Numbers(Sharp.PdfDocument document, params double[] values) =>
        new(document, [.. values.Select(v => (Sharp.PdfItem)new Sharp.PdfReal(v))]);

    /// <summary>An axial (type 2) or radial (type 3) shading from one colour to another, extended at both ends.</summary>
    private static Sharp.PdfDictionary Shading(Sharp.PdfDocument document, int type, double[] coords, double[] from, double[] to, string space = "/DeviceRGB")
    {
        var function = new Sharp.PdfDictionary(document);
        function.Elements.SetInteger("/FunctionType", 2);
        function.Elements["/Domain"] = Numbers(document, 0, 1);
        function.Elements["/C0"] = Numbers(document, from);
        function.Elements["/C1"] = Numbers(document, to);
        function.Elements.SetInteger("/N", 1);
        var shading = new Sharp.PdfDictionary(document);
        shading.Elements.SetInteger("/ShadingType", type);
        shading.Elements.SetName("/ColorSpace", space);
        shading.Elements["/Coords"] = Numbers(document, coords);
        shading.Elements["/Function"] = function;
        shading.Elements["/Extend"] = new Sharp.PdfArray(document, new Sharp.PdfBoolean(true), new Sharp.PdfBoolean(true));
        document.Internals.AddObject(shading);
        return shading;
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static string Attribute(XElement element, string descendant, string attribute) =>
        (string?)element.Descendants(A + descendant).FirstOrDefault()?.Attribute(attribute) ?? "";

    private static XElement Shape(XElement tree, Func<(double X, double Y, double Width, double Height), bool> at) =>
        tree.Elements(P + "spTree").Single().Elements().Single(e => e.Name.LocalName is "sp" or "pic" or "cxnSp" && at(Box(e)));

    private static List<XElement> Stack(XElement tree) =>
        [.. tree.Elements(P + "spTree").Single().Elements().Where(e => e.Name.LocalName is not ("nvGrpSpPr" or "grpSpPr"))];

    private static bool Near(double value, double expected, double slack = 2) => Math.Abs(value - expected) <= slack;

    // ───────────────────────── Turned text ─────────────────────────

    [Fact]
    public async Task Sideways_and_turned_text_become_turned_text_boxes()
    {
        var content = "BT /F1 20 Tf 300 480 Td (Upright title) Tj ET\n" +
                      "BT /F1 14 Tf 0 1 -1 0 60 100 Tm (Turned upwards) Tj ET\n" +
                      "BT /F1 14 Tf 0 -1 1 0 900 450 Tm (Turned downwards) Tj ET\n" +
                      "BT /F1 14 Tf 0.8660254 0.5 -0.5 0.8660254 500 150 Tm (Slanted by thirty) Tj ET\n" +
                      "BT /F1 14 Tf -1 0 0 -1 600 300 Tm (Upside down) Tj ET\n";
        var pptx = await ConvertAsync(RawPdf(Path.Combine(fx.NewFolder(), "turned.pdf"), content));
        PdfSlidesTests.ValidPackage(pptx);
        var (tree, _) = Assert.Single(Slides(pptx));

        Assert.Equal("", Attribute(TextBox(tree, "Upright title"), "xfrm", "rot"));
        // Rotation is clockwise on the slide: text running up the page is turned by 270°.
        var up = TextBox(tree, "Turned upwards");
        Assert.Equal("16200000", Attribute(up, "xfrm", "rot"));
        Assert.Equal("90", Rotation(TextBox(tree, "Turned downwards")));
        Assert.Equal("330", Rotation(TextBox(tree, "Slanted by thirty")));
        Assert.Equal("180", Rotation(TextBox(tree, "Upside down")));

        // The unturned box is as wide as the line and as high as the text; turned about its centre, it lies along the
        // line of text: left of x = 60 (the letters' tops point left), from 100 pt above the page's bottom upwards.
        var (x, y, width, height) = Box(up);
        Assert.InRange(width, 80, 125);
        Assert.InRange(height, 12, 22);
        Assert.InRange(x + width / 2, 50, 60);
        Assert.InRange(y + height / 2, Height - 100 - width / 2 - 6, Height - 100 - width / 2 + 6);

        // Filee's reader turns the box too, and the renderer draws the text where the PDF has it.
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        var turned = deck.Slides[0].Elements.OfType<SlideShape>().Single(s => s.Text is not null && Math.Abs(s.Rotation - 270) < 0.01);
        Assert.Contains("Turned upwards", string.Concat(turned.Text!.Paragraphs.SelectMany(p => p.Inlines).OfType<Hwp.Hwpx.HText>().Select(t => t.Text)));
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        Assert.True(Dark(bitmap, new SKRectI(46, (int)Height - 205, 62, (int)Height - 100)) > 20);
        Assert.True(Dark(bitmap, new SKRectI(100, (int)Height - 205, 140, (int)Height - 100)) == 0);
    }

    private static string Rotation(XElement shape) =>
        (long.Parse(Attribute(shape, "xfrm", "rot") is { Length: > 0 } rot ? rot : "0", System.Globalization.CultureInfo.InvariantCulture) / 60000)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int Dark(SKBitmap bitmap, SKRectI area)
    {
        var count = 0;
        for (var y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
        {
            for (var x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128)
                    count++;
            }
        }
        return count;
    }

    // ───────────────────────── Words far apart ─────────────────────────

    [Fact]
    public async Task Words_far_apart_on_a_baseline_are_boxes_of_their_own_and_justified_lines_stay_together()
    {
        var content =
            "BT /F1 12 Tf 40 500 Td (The page has a sentence of normal words with normal spaces between them all.) Tj ET\n" +
            // A label and its value far away (the document reading joins them with a tab).
            "BT /F1 12 Tf 40 460 Td (Revenue) Tj 260 0 Td ($5M) Tj ET\n" +
            // Two columns a little more than an em apart.
            "BT /F1 12 Tf 40 430 Td (Q1) Tj 32 0 Td (Q2) Tj ET\n" +
            // A numbered item keeps its number.
            "BT /F1 12 Tf 40 400 Td (1.) Tj 30 0 Td (Numbered item) Tj ET\n" +
            // A justified paragraph: wide spaces (word spacing) on its full lines, normal ones on the last.
            "BT /F1 12 Tf 15 TL 13 Tw 40 340 Td (alpha bravo charlie) Tj T* (alpha bravo charlie) Tj 0 Tw T* (delta) Tj ET\n";
        var pptx = await ConvertAsync(RawPdf(Path.Combine(fx.NewFolder(), "gaps.pdf"), content));
        var (tree, _) = Assert.Single(Slides(pptx));

        var label = TextBox(tree, "Revenue");
        Assert.DoesNotContain("$5M", Text(label), StringComparison.Ordinal);
        Assert.InRange(Box(TextBox(tree, "$5M")).X, 298, 302);
        Assert.DoesNotContain("Q2", Text(TextBox(tree, "Q1")), StringComparison.Ordinal);
        Assert.InRange(Box(TextBox(tree, "Q2")).X, 70, 74);
        Assert.Contains("Numbered item", Text(TextBox(tree, "1.")), StringComparison.Ordinal);
        var justified = TextBox(tree, "alpha");
        Assert.Equal("alpha bravo charlie alpha bravo charlie delta", System.Text.RegularExpressions.Regex.Replace(Text(justified), "\\s+", " ").Trim());
        Assert.Contains("normal words with normal spaces", Text(TextBox(tree, "The page has")), StringComparison.Ordinal);
    }

    // ───────────────────────── Gradients, transparency, patterns ─────────────────────────

    /// <summary>
    /// A page of fills: an axial gradient (red → blue) in a rectangle, a radial one (white → green) painted with "sh"
    /// through a round clip, a half-transparent green bar, a blue bar faded by a soft mask (a grey gradient) and a
    /// rectangle tiled with red squares.
    /// </summary>
    private static string MakeFillsPdf(string folder)
    {
        const string circle = "500 400 m 500 427.6 477.6 450 450 450 c 422.4 450 400 427.6 400 400 c 400 372.4 422.4 350 450 350 c 477.6 350 500 372.4 500 400 c h";
        var content =
            "q /Pattern cs /P1 scn 100 340 200 100 re f Q\n" +
            $"q {circle} W n /Sh1 sh Q\n" +
            "q /GS1 gs 0 0.5 0 rg 600 340 200 100 re f Q\n" +
            "q /GS2 gs 0 0 1 rg 100 100 200 100 re f Q\n" +
            "q /Pattern cs /T1 scn 600 100 200 100 re f Q\n";
        return RawPdf(Path.Combine(folder, "fills.pdf"), content, (document, page) =>
        {
            var pattern = new Sharp.PdfDictionary(document);
            pattern.Elements.SetName("/Type", "/Pattern");
            pattern.Elements.SetInteger("/PatternType", 2);
            pattern.Elements.SetReference("/Shading", Shading(document, 2, [100, 0, 300, 0], [1, 0, 0], [0, 0, 1]));
            Resource(document, page, "/Pattern", "/P1", pattern);
            Resource(document, page, "/Shading", "/Sh1", Shading(document, 3, [450, 400, 0, 450, 400, 50], [1, 1, 1], [0, 0.6, 0]));

            var half = new Sharp.PdfDictionary(document);
            half.Elements.SetName("/Type", "/ExtGState");
            half.Elements["/ca"] = new Sharp.PdfReal(0.5);
            Resource(document, page, "/ExtGState", "/GS1", half);

            // Soft mask: a group painting a grey gradient across the bar, black (hidden) to white (shown).
            var group = new Sharp.PdfDictionary(document);
            group.Elements.SetName("/Type", "/XObject");
            group.Elements.SetName("/Subtype", "/Form");
            group.Elements["/BBox"] = Numbers(document, 0, 0, Width, Height);
            var transparency = new Sharp.PdfDictionary(document);
            transparency.Elements.SetName("/S", "/Transparency");
            transparency.Elements.SetName("/CS", "/DeviceGray");
            group.Elements["/Group"] = transparency;
            var groupResources = new Sharp.PdfDictionary(document);
            var groupShadings = new Sharp.PdfDictionary(document);
            groupShadings.Elements.SetReference("/S0", Shading(document, 2, [100, 0, 300, 0], [0], [1], "/DeviceGray"));
            groupResources.Elements["/Shading"] = groupShadings;
            group.Elements["/Resources"] = groupResources;
            group.CreateStream("/S0 sh"u8.ToArray());
            document.Internals.AddObject(group);
            var mask = new Sharp.PdfDictionary(document);
            mask.Elements.SetName("/Type", "/Mask");
            mask.Elements.SetName("/S", "/Luminosity");
            mask.Elements.SetReference("/G", group);
            var faded = new Sharp.PdfDictionary(document);
            faded.Elements.SetName("/Type", "/ExtGState");
            faded.Elements["/SMask"] = mask;
            Resource(document, page, "/ExtGState", "/GS2", faded);

            // Tiles of 10 × 10 points with a red square of 5 × 5 in their bottom-left corner.
            var tile = new Sharp.PdfDictionary(document);
            tile.Elements.SetName("/Type", "/Pattern");
            tile.Elements.SetInteger("/PatternType", 1);
            tile.Elements.SetInteger("/PaintType", 1);
            tile.Elements.SetInteger("/TilingType", 1);
            tile.Elements["/BBox"] = Numbers(document, 0, 0, 10, 10);
            tile.Elements.SetInteger("/XStep", 10);
            tile.Elements.SetInteger("/YStep", 10);
            tile.Elements["/Resources"] = new Sharp.PdfDictionary(document);
            tile.CreateStream("1 0 0 rg 0 0 5 5 re f"u8.ToArray());
            Resource(document, page, "/Pattern", "/T1", tile);
        });
    }

    [Fact]
    public async Task Shadings_transparency_and_soft_masks_become_drawingml_fills()
    {
        var pptx = await ConvertAsync(MakeFillsPdf(fx.NewFolder()));
        PdfSlidesTests.ValidPackage(pptx);
        var (tree, _) = Assert.Single(Slides(pptx));

        // The axial shading: a rectangle with a linear gradient from red to blue, left to right.
        var axial = Shape(tree, b => Near(b.X, 100) && Near(b.Y, 100));
        Assert.Equal("rect", Attribute(axial, "prstGeom", "prst"));
        var stops = axial.Descendants(A + "gs").Select(g => (string)g.Element(A + "srgbClr")!.Attribute("val")!).ToList();
        Assert.Equal("FF0000", stops[0]);
        Assert.Equal("0000FF", stops[^1]);
        Assert.Equal("0", Attribute(axial, "lin", "ang"));

        // The radial shading through a round clip: the circle's own outline, a circular gradient from its centre.
        var radial = Shape(tree, b => Near(b.X, 400) && Near(b.Y, Height - 450) && Near(b.Width, 100));
        Assert.NotNull(radial.Descendants(A + "custGeom").SingleOrDefault());
        Assert.Equal("circle", (string?)radial.Descendants(A + "gradFill").Single().Element(A + "path")!.Attribute("path"));
        Assert.Equal("50000", Attribute(radial, "fillToRect", "l"));
        Assert.Equal("FFFFFF", (string?)radial.Descendants(A + "gs").First().Element(A + "srgbClr")!.Attribute("val"));

        // ca 0.5: a half-transparent bar.
        var half = Shape(tree, b => Near(b.X, 600) && Near(b.Y, 100));
        Assert.Equal("50000", Attribute(half, "alpha", "val"));

        // The soft mask's grey gradient fades the blue bar in from the left: blue stops, transparent to opaque.
        var faded = Shape(tree, b => Near(b.X, 100) && Near(b.Y, Height - 200));
        var alphas = faded.Descendants(A + "gs").Select(g => (int?)g.Descendants(A + "alpha").SingleOrDefault()?.Attribute("val") ?? 100000).ToList();
        Assert.All(faded.Descendants(A + "gs"), g => Assert.Equal("0000FF", (string?)g.Element(A + "srgbClr")!.Attribute("val")));
        Assert.True(alphas[0] < 5000 && alphas[^1] > 95000, string.Join(", ", alphas));

        // Filee's renderer shows them: reddish to bluish, white in the circle's centre, light green half-transparent
        // bar, the faded bar paler on the left, red tiles in their cell corners.
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 144, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)(x * 2), (int)(y * 2));
        Assert.True(At(105, Height - 390).Red > 200 && At(105, Height - 390).Blue < 60, At(105, Height - 390).ToString());
        Assert.True(At(295, Height - 390).Blue > 200 && At(295, Height - 390).Red < 60, At(295, Height - 390).ToString());
        Assert.True(At(450, Height - 400) is { Red: > 230, Green: > 230, Blue: > 230 }, At(450, Height - 400).ToString());
        Assert.True(At(700, Height - 390) is { Red: > 100 and < 160, Green: > 160, Blue: > 100 and < 160 }, At(700, Height - 390).ToString());
        Assert.True(At(110, Height - 150).Blue > 200 && At(110, Height - 150).Red > 200, At(110, Height - 150).ToString());
        Assert.True(At(290, Height - 150) is { Blue: > 200, Red: < 60 }, At(290, Height - 150).ToString());
        Assert.True(At(602.5, Height - 102.5) is { Red: > 200, Green: < 60 }, At(602.5, Height - 102.5).ToString());
        Assert.True(At(607.5, Height - 107.5) is { Red: > 230, Green: > 230 }, At(607.5, Height - 107.5).ToString());
    }

    [Fact]
    public async Task Pdf_fills_reach_odg_through_the_rebuilt_slides()
    {
        var job = await RunAsync(MakeFillsPdf(fx.NewFolder()), new Preset { TargetFormat = "odg" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var drawing = Odf.Draw.OdfDrawReader.Read(job.Outputs.Single(), cancellationToken: Token);
        var shapes = drawing.Slides[0].Elements.OfType<SlideShape>().ToList();
        Assert.Contains(shapes, s => s.Fill is GradientFill { Shape: GradientShape.Linear });
        Assert.Contains(shapes, s => s.Fill is SolidFill { Color.Alpha: > 100 and < 160 });
    }

    // ───────────────────────── Pictures: turned, mirrored, clipped, stencils ─────────────────────────

    [Fact]
    public async Task Pictures_are_turned_mirrored_and_clipped_as_drawn_and_stencil_masks_keep_their_colour()
    {
        // A 40 × 2 picture: its left half red, its right half blue.
        const string ellipse = "250 100 m 250 127.6 227.6 150 200 150 c 172.4 150 150 127.6 150 100 c 150 72.4 172.4 50 200 50 c 227.6 50 250 72.4 250 100 c h";
        var content =
            "q 200 0 0 100 100 300 cm /Im0 Do Q\n" +                 // upright
            "q 0 200 -100 0 500 300 cm /Im0 Do Q\n" +                // turned: its left edge at the bottom, running up
            "q 0.9396926 0.3420201 -0.1710101 0.4698463 560 120 cm 100 0 0 100 0 0 cm /Im0 Do Q\n" +                // turned by 20 degrees, 100 x 50
            "q -200 0 0 100 900 50 cm /Im0 Do Q\n" +                 // mirrored
            "q 700 300 60 100 re W n 200 0 0 100 650 300 cm /Im0 Do Q\n" + // cut by a rectangle
            $"q {ellipse} W n 100 0 0 100 150 50 cm /Im0 Do Q\n" + // cut by an ellipse
            "q 1 0 0 rg 80 0 0 40 520 400 cm /Mk Do Q\n";           // a stencil mask, painted red
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "pictures.pdf"), content, (document, page) =>
        {
            var picture = new Sharp.PdfDictionary(document);
            picture.Elements.SetName("/Type", "/XObject");
            picture.Elements.SetName("/Subtype", "/Image");
            picture.Elements.SetInteger("/Width", 40);
            picture.Elements.SetInteger("/Height", 2);
            picture.Elements.SetInteger("/BitsPerComponent", 8);
            picture.Elements.SetName("/ColorSpace", "/DeviceRGB");
            byte[] row = [.. Enumerable.Range(0, 40).SelectMany(x => x < 20 ? new byte[] { 255, 0, 0 } : [0, 0, 255])];
            picture.CreateStream([.. row, .. row]);
            Resource(document, page, "/XObject", "/Im0", picture);
            var stencil = new Sharp.PdfDictionary(document);
            stencil.Elements.SetName("/Type", "/XObject");
            stencil.Elements.SetName("/Subtype", "/Image");
            stencil.Elements.SetInteger("/Width", 8);
            stencil.Elements.SetInteger("/Height", 4);
            stencil.Elements.SetInteger("/BitsPerComponent", 1);
            stencil.Elements["/ImageMask"] = new Sharp.PdfBoolean(true);
            // Samples of 0 paint: the left half of every row.
            stencil.CreateStream([0x0F, 0x0F, 0x0F, 0x0F]);
            Resource(document, page, "/XObject", "/Mk", stencil);
        });
        var pptx = await ConvertAsync(pdf);
        PdfSlidesTests.ValidPackage(pptx);
        var (tree, _) = Assert.Single(Slides(pptx));
        var pictures = tree.Descendants(P + "pic").ToList();
        Assert.Equal(7, pictures.Count);

        var upright = pictures.Single(p => Near(Box(p).X, 100) && Near(Box(p).Y, Height - 400));
        Assert.Equal("", Attribute(upright, "xfrm", "rot"));
        // Turned: a 200 × 100 box turned by 270° about its centre (450, 140 from the top).
        var turned = pictures.Single(p => Attribute(p, "xfrm", "rot") == "16200000");
        var box = Box(turned);
        Assert.Equal((200, 100), (Math.Round(box.Width), Math.Round(box.Height)));
        Assert.Equal((450, 140), (Math.Round(box.X + box.Width / 2), Math.Round(box.Y + box.Height / 2)));
        Assert.Contains(pictures, p => Attribute(p, "xfrm", "flipH") == "1" && Near(Box(p).X, 700) && Near(Box(p).Width, 200));
        // Turned by 20° anticlockwise in the PDF: 340° clockwise on the slide, its size kept.
        var slanted = pictures.Single(p => Attribute(p, "xfrm", "rot") == "20400000");
        Assert.Equal((100, 50), (Math.Round(Box(slanted).Width), Math.Round(Box(slanted).Height)));
        // The clipping rectangle crops the picture: 50 of its 200 points off the left, 90 off the right.
        var cropped = pictures.Single(p => p.Descendants(A + "srcRect").Any());
        Assert.Equal(("25000", "45000"), (Attribute(cropped, "srcRect", "l"), Attribute(cropped, "srcRect", "r")));
        Assert.Equal((700, 60), (Math.Round(Box(cropped).X), Math.Round(Box(cropped).Width)));
        // The ellipse is the outline of its picture.
        Assert.Single(pictures, p => p.Descendants(A + "custGeom").Any());

        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        bool Red(int x, double y) => bitmap.GetPixel(x, (int)y) is { Red: > 200, Green: < 60, Blue: < 60 };
        bool Blue(int x, double y) => bitmap.GetPixel(x, (int)y) is { Red: < 60, Green: < 60, Blue: > 200 };
        bool White(int x, double y) => bitmap.GetPixel(x, (int)y) is { Red: > 230, Green: > 230, Blue: > 230 };
        Assert.True(Red(130, 190) && Blue(270, 190));             // upright
        Assert.True(Red(450, 220) && Blue(450, 60));              // turned: its left half low on the page
        Assert.True(Red(870, Height - 100) && Blue(730, Height - 100)); // mirrored
        Assert.True(Red(705, 190) && Blue(757, 190) && White(690, 190) && White(770, 190)); // cropped
        Assert.True(White(153, Height - 147) && Red(185, Height - 100) && Blue(215, Height - 100)); // ellipse
        Assert.True(Red(530, Height - 420) && White(590, Height - 420) && White(570, Height - 420)); // stencil
    }

    // ───────────────────────── Paint order ─────────────────────────

    [Fact]
    public async Task Text_shapes_and_pictures_stack_in_the_pdfs_paint_order()
    {
        var content =
            "BT /F1 18 Tf 100 395 Td (Under the bar) Tj ET\n" +
            "q 0.9 0.2 0.2 rg 90 380 300 40 re f Q\n" +
            "BT /F1 10 Tf 1 g 260 384 Td (On the bar) Tj ET\n" +
            // A curve between two rectangles, the second over it: drawn pictures split around what covers them.
            "q 0 0 1 rg 100 100 m 150 250 250 250 300 100 c f Q\n" +
            "q 0 1 0 rg 150 120 100 40 re f Q\n" +
            "q 1 0 1 rg 200 100 m 250 250 350 250 400 100 c f Q\n";
        var pptx = await ConvertAsync(RawPdf(Path.Combine(fx.NewFolder(), "order.pdf"), content));
        var (tree, _) = Assert.Single(Slides(pptx));
        var stack = Stack(tree);

        var under = stack.IndexOf(TextBox(tree, "Under the bar"));
        var bar = stack.IndexOf(Shape(tree, b => Near(b.X, 90) && Near(b.Y, Height - 420)));
        var on = stack.IndexOf(TextBox(tree, "On the bar"));
        Assert.True(under < bar && bar < on, $"{under} {bar} {on}");

        // The blue curve, the green bar on it, then the magenta curve over the bar: two pictures of drawn graphics, the
        // bar between them (one picture would put the magenta curve under the bar).
        var green = stack.IndexOf(Shape(tree, b => Near(b.X, 150) && Near(b.Y, Height - 160)));
        var pictures = stack.Where(e => e.Name.LocalName == "pic").Select(e => stack.IndexOf(e)).ToList();
        Assert.Equal(2, pictures.Count);
        Assert.True(pictures[0] < green && green < pictures[1], $"{pictures[0]} {green} {pictures[1]}");
    }

    // ───────────────────────── Speaker notes ─────────────────────────

    [Fact]
    public async Task Comments_of_the_page_become_speaker_notes()
    {
        var folder = fx.NewFolder();
        var path = RawPdf(Path.Combine(folder, "notes.pdf"), "BT /F1 20 Tf 100 400 Td (A slide with comments) Tj ET\n", (document, page) =>
        {
            Sharp.PdfDictionary Annotation(string subtype, string text, string? author, double top)
            {
                var annotation = new Sharp.PdfDictionary(document);
                annotation.Elements.SetName("/Type", "/Annot");
                annotation.Elements.SetName("/Subtype", subtype);
                annotation.Elements["/Rect"] = Numbers(document, 50, top - 20, 70, top);
                annotation.Elements.SetString("/Contents", text);
                if (author is not null)
                    annotation.Elements.SetString("/T", author);
                if (subtype == "/FreeText")
                    annotation.Elements.SetString("/DA", "/Helv 12 Tf 0 g");
                document.Internals.AddObject(annotation);
                return annotation;
            }
            var annotations = new Sharp.PdfArray(document);
            annotations.Elements.Add(Annotation("/FreeText", "Say the numbers slowly", null, 300).Reference!);
            annotations.Elements.Add(Annotation("/Text", "Check the Q3 figures", "Ana", 500).Reference!);
            page.Elements["/Annots"] = annotations;
        });
        var pptx = await ConvertAsync(path);
        PdfSlidesTests.ValidPackage(pptx);

        using var zip = ZipFile.OpenRead(pptx);
        var rels = XDocument.Load(zip.GetEntry("ppt/slides/_rels/slide1.xml.rels")!.Open()).Root!.Elements().Select(e => (string)e.Attribute("Target")!);
        Assert.Contains("../notesSlides/notesSlide1.xml", rels);
        var notes = XDocument.Load(zip.GetEntry("ppt/notesSlides/notesSlide1.xml")!.Open()).Descendants(A + "t").Select(t => t.Value).ToList();
        // Top to bottom, with the author.
        Assert.Equal(["Ana: Check the Q3 figures", "Say the numbers slowly"], notes);
        Assert.NotNull(zip.GetEntry("ppt/notesMasters/notesMaster1.xml"));

        // A page without comments has no notes page.
        var plain = await ConvertAsync(RawPdf(Path.Combine(folder, "plain.pdf"), "BT /F1 20 Tf 100 400 Td (No comments) Tj ET\n"));
        using var plainZip = ZipFile.OpenRead(plain);
        Assert.Null(plainZip.GetEntry("ppt/notesSlides/notesSlide1.xml"));
    }

    // ───────────────────────── Function and mesh shadings, drawn masks, tiles, blend modes ─────────────────────────

    /// <summary>A free-form, Coons or lattice mesh (types 4, 6, 5) of RGB vertices, 16-bit coordinates over the page, 8-bit colours.</summary>
    private static Sharp.PdfDictionary Mesh(Sharp.PdfDocument document, int type, byte[] data, int perRow = 0)
    {
        var mesh = new Sharp.PdfDictionary(document);
        mesh.Elements.SetInteger("/ShadingType", type);
        mesh.Elements.SetName("/ColorSpace", "/DeviceRGB");
        mesh.Elements.SetInteger("/BitsPerCoordinate", 16);
        mesh.Elements.SetInteger("/BitsPerComponent", 8);
        if (type == 5)
            mesh.Elements.SetInteger("/VerticesPerRow", perRow);
        else
            mesh.Elements.SetInteger("/BitsPerFlag", 8);
        mesh.Elements["/Decode"] = Numbers(document, 0, Width, 0, Height, 0, 1, 0, 1, 0, 1);
        mesh.CreateStream(data);
        document.Internals.AddObject(mesh);
        return mesh;
    }

    /// <summary>A point of a mesh (16 bits each over the page) and, when given, an RGB colour.</summary>
    private static byte[] MeshPoint(double x, double y, (byte R, byte G, byte B)? color = null)
    {
        var (u, v) = ((ushort)Math.Round(x / Width * 65535), (ushort)Math.Round(y / Height * 65535));
        byte[] point = [(byte)(u >> 8), (byte)u, (byte)(v >> 8), (byte)v];
        return color is { } c ? [.. point, c.R, c.G, c.B] : point;
    }

    /// <summary>A function-based shading (type 1) of a 2 × 2 sampled function over the unit square, mapped by <paramref name="matrix"/>.</summary>
    private static Sharp.PdfDictionary FunctionShading(Sharp.PdfDocument document, double[] matrix, params (byte R, byte G, byte B)[] corners)
    {
        var function = new Sharp.PdfDictionary(document);
        function.Elements.SetInteger("/FunctionType", 0);
        function.Elements["/Domain"] = Numbers(document, 0, 1, 0, 1);
        function.Elements["/Range"] = Numbers(document, 0, 1, 0, 1, 0, 1);
        function.Elements["/Size"] = new Sharp.PdfArray(document, new Sharp.PdfInteger(2), new Sharp.PdfInteger(2));
        function.Elements.SetInteger("/BitsPerSample", 8);
        function.CreateStream([.. corners.SelectMany(c => new[] { c.R, c.G, c.B })]);
        document.Internals.AddObject(function);
        var shading = new Sharp.PdfDictionary(document);
        shading.Elements.SetInteger("/ShadingType", 1);
        shading.Elements.SetName("/ColorSpace", "/DeviceRGB");
        shading.Elements["/Matrix"] = Numbers(document, matrix);
        shading.Elements.SetReference("/Function", function);
        document.Internals.AddObject(shading);
        return shading;
    }

    /// <summary>A form group for a soft mask painting <paramref name="content"/> over the page, with <paramref name="shadings"/>.</summary>
    private static Sharp.PdfDictionary MaskState(Sharp.PdfDocument document, string content, params (string Name, Sharp.PdfDictionary Shading)[] shadings)
    {
        var group = new Sharp.PdfDictionary(document);
        group.Elements.SetName("/Type", "/XObject");
        group.Elements.SetName("/Subtype", "/Form");
        group.Elements["/BBox"] = Numbers(document, 0, 0, Width, Height);
        var transparency = new Sharp.PdfDictionary(document);
        transparency.Elements.SetName("/S", "/Transparency");
        transparency.Elements.SetName("/CS", "/DeviceRGB");
        group.Elements["/Group"] = transparency;
        var resources = new Sharp.PdfDictionary(document);
        var named = new Sharp.PdfDictionary(document);
        foreach (var (name, shading) in shadings)
            named.Elements.SetReference(name, shading);
        resources.Elements["/Shading"] = named;
        group.Elements["/Resources"] = resources;
        group.CreateStream(Encoding.ASCII.GetBytes(content));
        document.Internals.AddObject(group);
        var mask = new Sharp.PdfDictionary(document);
        mask.Elements.SetName("/Type", "/Mask");
        mask.Elements.SetName("/S", "/Luminosity");
        mask.Elements.SetReference("/G", group);
        var state = new Sharp.PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/SMask"] = mask;
        return state;
    }

    /// <summary>
    /// A page of what the slides now rebuild: a function-based shading that is a linear gradient (red → blue) and one
    /// that isn't (a saddle of red and blue), a free-form mesh that is a gradient (green → yellow), a Coons patch of
    /// four colours, a lattice mesh as a pattern fill (orange → purple), a red bar under a luminosity mask drawn from
    /// two rectangles (white, grey), a green picture under a gradient mask, and a yellow Multiply highlight over text
    /// beside a blue Screen bar.
    /// </summary>
    internal static string MakePaintingPdf(string folder)
    {
        var content =
            "q 100 340 200 100 re W n /Sh1 sh Q\n" +
            "q 400 340 200 100 re W n /Sh2 sh Q\n" +
            "q 655 345 190 90 re W n /Sh3 sh Q\n" +
            "q /Sh4 sh Q\n" +
            "q /Pattern cs /P5 scn 410 110 180 80 re f Q\n" +
            "q /GS1 gs 1 0 0 rg 650 240 260 40 re f Q\n" +
            "q /GS2 gs 200 0 0 50 650 120 cm /Im0 Do Q\n" +
            "BT /F1 24 Tf 100 490 Td (Highlighted words) Tj ET\n" +
            "q /GS3 gs 1 1 0 rg 95 482 230 30 re f Q\n" +
            "q /GS4 gs 0 0 1 rg 600 482 100 30 re f Q\n";
        return RawPdf(Path.Combine(folder, "painting.pdf"), content, (document, page) =>
        {
            Resource(document, page, "/Shading", "/Sh1", FunctionShading(document, [200, 0, 0, 100, 100, 340], (255, 0, 0), (0, 0, 255), (255, 0, 0), (0, 0, 255)));
            Resource(document, page, "/Shading", "/Sh2", FunctionShading(document, [200, 0, 0, 100, 400, 340], (255, 0, 0), (0, 0, 255), (0, 0, 255), (255, 0, 0)));
            // Two triangles over 650–850 × 340–440: green on the left, yellow on the right.
            byte[] freeForm =
            [
                0, .. MeshPoint(650, 340, (0, 160, 0)), 0, .. MeshPoint(850, 340, (255, 220, 0)), 0, .. MeshPoint(650, 440, (0, 160, 0)),
                1, .. MeshPoint(850, 440, (255, 220, 0)),
            ];
            Resource(document, page, "/Shading", "/Sh3", Mesh(document, 4, freeForm));
            // A Coons patch over 100–300 × 100–200: red, green, blue and white corners.
            byte[] coons =
            [
                0, .. MeshPoint(100, 100), .. MeshPoint(100, 133), .. MeshPoint(100, 167), .. MeshPoint(100, 200), .. MeshPoint(167, 200),
                .. MeshPoint(233, 200), .. MeshPoint(300, 200), .. MeshPoint(300, 167), .. MeshPoint(300, 133), .. MeshPoint(300, 100),
                .. MeshPoint(233, 100), .. MeshPoint(167, 100), 255, 0, 0, 0, 160, 0, 0, 0, 255, 255, 255, 255,
            ];
            Resource(document, page, "/Shading", "/Sh4", Mesh(document, 6, coons));
            byte[] lattice =
            [
                .. MeshPoint(400, 100, (255, 140, 0)), .. MeshPoint(600, 100, (255, 140, 0)),
                .. MeshPoint(400, 200, (120, 0, 160)), .. MeshPoint(600, 200, (120, 0, 160)),
            ];
            var pattern = new Sharp.PdfDictionary(document);
            pattern.Elements.SetName("/Type", "/Pattern");
            pattern.Elements.SetInteger("/PatternType", 2);
            pattern.Elements.SetReference("/Shading", Mesh(document, 5, lattice, perRow: 2));
            Resource(document, page, "/Pattern", "/P5", pattern);

            Resource(document, page, "/ExtGState", "/GS1", MaskState(document, "1 g 0 0 780 540 re f 0.5 g 780 0 180 540 re f"));
            Resource(document, page, "/ExtGState", "/GS2", MaskState(document, "/S0 sh", ("/S0", Shading(document, 2, [650, 0, 850, 0], [0, 0, 0], [1, 1, 1]))));
            var picture = new Sharp.PdfDictionary(document);
            picture.Elements.SetName("/Type", "/XObject");
            picture.Elements.SetName("/Subtype", "/Image");
            picture.Elements.SetInteger("/Width", 40);
            picture.Elements.SetInteger("/Height", 2);
            picture.Elements.SetInteger("/BitsPerComponent", 8);
            picture.Elements.SetName("/ColorSpace", "/DeviceRGB");
            picture.CreateStream([.. Enumerable.Range(0, 80).SelectMany(_ => new byte[] { 0, 150, 60 })]);
            Resource(document, page, "/XObject", "/Im0", picture);
            foreach (var (name, mode) in new[] { ("/GS3", "/Multiply"), ("/GS4", "/Screen") })
            {
                var blend = new Sharp.PdfDictionary(document);
                blend.Elements.SetName("/Type", "/ExtGState");
                blend.Elements.SetName("/BM", mode);
                Resource(document, page, "/ExtGState", name, blend);
            }
        });
    }

    [Fact]
    public async Task Function_and_mesh_shadings_become_gradients_where_they_are_one_and_pictures_elsewhere()
    {
        var pptx = await ConvertAsync(MakePaintingPdf(fx.NewFolder()));
        PdfSlidesTests.ValidPackage(pptx);
        var (tree, _) = Assert.Single(Slides(pptx));
        SKColor Stop(XElement shape, int index) => SKColor.Parse((string)shape.Descendants(A + "gs").ElementAt(index).Element(A + "srgbClr")!.Attribute("val")!);

        // The function-based shading that changes along x only: a linear gradient, red to blue, left to right.
        var linear = Shape(tree, b => Near(b.X, 100) && Near(b.Y, Height - 440) && Near(b.Width, 200));
        Assert.Equal("0", Attribute(linear, "lin", "ang"));
        var (first, last) = (Stop(linear, 0), Stop(linear, linear.Descendants(A + "gs").Count() - 1));
        Assert.True(first is { Red: > 230, Blue: < 20 } && last is { Red: < 20, Blue: > 230 }, $"{first} {last}");
        // The free-form mesh: green to yellow, left to right.
        var mesh = Shape(tree, b => Near(b.X, 655) && Near(b.Y, Height - 435));
        Assert.NotNull(mesh.Descendants(A + "gradFill").SingleOrDefault());
        // The lattice as a pattern fill: a gradient across the rectangle, orange at the bottom, purple at the top.
        var lattice = Shape(tree, b => Near(b.X, 410) && Near(b.Y, Height - 190));
        Assert.NotNull(lattice.Descendants(A + "gradFill").SingleOrDefault());
        // The saddle and the Coons patch aren't gradients: no shapes of their own.
        Assert.DoesNotContain(Stack(tree), e => e.Name.LocalName == "sp" && e.Element(P + "txBody") is null && Near(Box(e).X, 400) && Near(Box(e).Y, Height - 440));
        Assert.DoesNotContain(Stack(tree), e => e.Name.LocalName == "sp" && e.Element(P + "txBody") is null && Near(Box(e).X, 100) && Near(Box(e).Y, Height - 200));

        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)x, (int)(Height - y));
        bool Red(SKColor c) => c is { Red: > 190, Green: < 80, Blue: < 80 };
        bool Blue(SKColor c) => c is { Red: < 80, Green: < 80, Blue: > 190 };
        Assert.True(Red(At(105, 390)) && Blue(At(295, 390)), $"{At(105, 390)} {At(295, 390)}");
        // The saddle: red at two opposite corners, blue at the others, purple in the middle.
        Assert.True(Red(At(404, 344)) && Red(At(596, 436)) && Blue(At(596, 344)) && Blue(At(404, 436)), $"{At(404, 344)} {At(596, 344)}");
        Assert.True(At(500, 390) is { Red: > 90 and < 170, Blue: > 90 and < 170 }, At(500, 390).ToString());
        Assert.True(At(660, 390) is { Green: > 120, Red: < 60 } && At(840, 390) is { Red: > 230, Green: > 190, Blue: < 60 }, $"{At(660, 390)} {At(840, 390)}");
        // The Coons patch: each corner's colour near it.
        Assert.True(Red(At(104, 104)) && Blue(At(296, 196)), $"{At(104, 104)} {At(296, 196)}");
        Assert.True(At(104, 196) is { Green: > 120, Red: < 60, Blue: < 60 } && At(296, 104) is { Red: > 220, Green: > 220, Blue: > 220 }, $"{At(104, 196)} {At(296, 104)}");
        Assert.True(At(500, 115) is { Red: > 220, Green: > 110 and < 170 } && At(500, 185) is { Red: > 100 and < 150, Blue: > 130 }, $"{At(500, 115)} {At(500, 185)}");
    }

    [Fact]
    public async Task Soft_masks_of_drawn_groups_and_gradient_masks_on_pictures_keep_their_alpha()
    {
        var pptx = await ConvertAsync(MakePaintingPdf(fx.NewFolder()));
        var (tree, rels) = Assert.Single(Slides(pptx));

        // The picture under the grey gradient mask: its own PNG, transparent on the left, opaque on the right.
        var picture = tree.Descendants(P + "pic").Single(p => Near(Box(p).X, 650) && Near(Box(p).Width, 200));
        using (var zip = ZipFile.OpenRead(pptx))
        {
            var target = rels[(string)picture.Descendants(A + "blip").Single().Attribute(R + "embed")!].Replace("../", "ppt/", StringComparison.Ordinal);
            using var stream = zip.GetEntry(target)!.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            using var png = SKBitmap.Decode(memory.ToArray());
            Assert.True(png.GetPixel(1, 0).Alpha < 30 && png.GetPixel(png.Width - 2, 0).Alpha > 225, $"{png.GetPixel(1, 0)} {png.GetPixel(png.Width - 2, 0)}");
        }

        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)x, (int)(Height - y));
        // The red bar: full red where the mask's group is white, half red where it is grey.
        Assert.True(At(700, 260) is { Red: > 230, Green: < 40, Blue: < 40 }, At(700, 260).ToString());
        Assert.True(At(880, 260) is { Red: > 230, Green: > 100 and < 160, Blue: > 100 and < 160 }, At(880, 260).ToString());
        // The masked picture fades in from the left.
        Assert.True(At(655, 145).Green > 240 && At(845, 145) is { Green: > 130 and < 170, Red: < 30 }, $"{At(655, 145)} {At(845, 145)}");
    }

    [Fact]
    public async Task Tiling_patterns_are_tiled_picture_fills_and_multiply_highlights_go_under_their_text()
    {
        var folder = fx.NewFolder();
        var (tree, rels) = Assert.Single(Slides(await ConvertAsync(MakeFillsPdf(folder))));
        // The rectangle tiled with red squares: a picture fill of its cell (10 points, a 30-pixel PNG at 96 dpi: 44 %).
        var tiled = Shape(tree, b => Near(b.X, 600) && Near(b.Y, Height - 200));
        var tile = tiled.Descendants(A + "tile").Single();
        Assert.InRange((int)tile.Attribute("sx")!, 44000, 45000);
        Assert.InRange((int)tile.Attribute("sy")!, 44000, 45000);
        Assert.Equal("tl", (string?)tile.Attribute("algn"));
        Assert.EndsWith(".png", rels[(string)tiled.Descendants(A + "blip").Single().Attribute(R + "embed")!], StringComparison.Ordinal);

        var painting = await ConvertAsync(MakePaintingPdf(folder));
        var (stack, _) = Assert.Single(Slides(painting));
        var shapes = Stack(stack);
        // Multiply: the yellow highlight goes under the text it was painted over.
        var highlight = shapes.IndexOf(Shape(stack, b => Near(b.X, 95) && Near(b.Y, Height - 512)));
        Assert.True(highlight < shapes.IndexOf(TextBox(stack, "Highlighted")), $"{highlight}");
        // Screen can't be a shape: it is drawn, screened over the white page (white, as in the PDF).
        Assert.DoesNotContain(shapes, e => e.Name.LocalName == "sp" && Near(Box(e).X, 600) && Near(Box(e).Y, Height - 512));
        var deck = PptxSlideReader.Read(painting, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        Assert.True(bitmap.GetPixel(320, (int)(Height - 500)) is { Red: > 230, Green: > 230, Blue: < 60 });
        Assert.True(bitmap.GetPixel(650, (int)(Height - 497)) is { Red: > 240, Green: > 240, Blue: > 240 }, bitmap.GetPixel(650, (int)(Height - 497)).ToString());
    }

    /// <summary>The colour of the photo-like picture of <see cref="MakeBlendPdf"/> at a point of the page (PDF coordinates).</summary>
    private static (int R, int G, int B) Photo(double x, double y) =>
        ((int)((x - 100) / 400 * 64) * 4, (int)((460 - y) / 200 * 32) * 8, 128);

    /// <summary>
    /// A page of blends over what is under them: a photo-like picture (red across, green down) and black text, a blue
    /// Screen bar over the right of the picture and over the text, an orange Overlay bar over the left of the picture,
    /// and a dark blue picture drawn with Screen over a red rectangle.
    /// </summary>
    internal static string MakeBlendPdf(string folder)
    {
        var content =
            "q 400 0 0 200 100 260 cm /Ph Do Q\n" +
            "BT /F1 36 Tf 120 120 Td (Backdrop text) Tj ET\n" +
            "q /GS1 gs 0 0 1 rg 300 100 120 380 re f Q\n" +
            "q /GS2 gs 1 0.5 0 rg 150 280 100 160 re f Q\n" +
            "1 0 0 rg 600 300 200 100 re f\n" +
            "q /GS1 gs 200 0 0 100 600 300 cm /Im1 Do Q\n";
        return RawPdf(Path.Combine(folder, "blend.pdf"), content, (document, page) =>
        {
            Resource(document, page, "/XObject", "/Ph", RgbPicture(document, 64, 32, i => (i % 64 * 4, i / 64 * 8, 128)));
            Resource(document, page, "/XObject", "/Im1", RgbPicture(document, 2, 2, _ => (0, 0, 128)));
            foreach (var (name, mode) in new[] { ("/GS1", "/Screen"), ("/GS2", "/Overlay") })
                Resource(document, page, "/ExtGState", name, BlendState(document, mode));
        });
    }

    /// <summary>
    /// A page of what a backdrop used to miss, each under a blue Screen bar: a JBIG2 picture (left half black, right
    /// half white) PdfPig can't decode, a red Type 3 glyph whose procedure paints a 30 pt square but whose text is ".",
    /// and an unembedded Arial code whose text is the ligature "ffi" (60 pt, 800/1000 em wide).
    /// </summary>
    internal static string MakeBackdropsPdf(string folder)
    {
        var content =
            "q 200 0 0 200 60 200 cm /Jb Do Q\n" +
            "BT /T3 40 Tf 1 0 0 rg 400 250 Td (.) Tj ET\n" +
            "BT /Lg 60 Tf 0 0 0 rg 600 250 Td <01> Tj ET\n" +
            "q /GS1 gs 0 0 1 rg 40 180 820 120 re f Q\n";
        return RawPdf(Path.Combine(folder, "backdrops.pdf"), content, (document, page) =>
        {
            // JBIG2: a 32 × 64 black shape at the left of a 64 × 64 picture.
            var shape = new Filee.Engines.Djvu.Jb2Shape(32, 64);
            Array.Fill(shape.Bits, (byte)1);
            shape.ComputeBox();
            var data = Filee.Engines.Djvu.Jbig2Encoder.Encode(64, 64, [shape], [new Filee.Engines.Djvu.Jb2Blit(0, 0, 0)], Token);
            var picture = new Sharp.PdfDictionary(document);
            picture.Elements.SetName("/Type", "/XObject");
            picture.Elements.SetName("/Subtype", "/Image");
            picture.Elements.SetInteger("/Width", 64);
            picture.Elements.SetInteger("/Height", 64);
            picture.Elements.SetInteger("/BitsPerComponent", 1);
            picture.Elements.SetName("/ColorSpace", "/DeviceGray");
            picture.Elements.SetName("/Filter", "/JBIG2Decode");
            picture.CreateStream(data);
            Resource(document, page, "/XObject", "/Jb", picture);
            Resource(document, page, "/Font", "/T3", Type3Font(document, "1000 0 0 0 750 750 d1 0 0 750 750 re f"));
            Resource(document, page, "/Font", "/Lg", LigatureFont(document));
            Resource(document, page, "/ExtGState", "/GS1", BlendState(document, "/Screen"));
        });
    }

    /// <summary>A Type 3 font whose code 46 (".") draws <paramref name="procedure"/> (glyph space of 1000 units an em).</summary>
    private static Sharp.PdfDictionary Type3Font(Sharp.PdfDocument document, string procedure)
    {
        var glyph = new Sharp.PdfDictionary(document);
        glyph.CreateStream(Encoding.ASCII.GetBytes(procedure));
        document.Internals.AddObject(glyph);
        var procedures = new Sharp.PdfDictionary(document);
        procedures.Elements.SetReference("/period", glyph);
        var encoding = new Sharp.PdfDictionary(document);
        encoding.Elements.SetName("/Type", "/Encoding");
        encoding.Elements["/Differences"] = new Sharp.PdfArray(document, new Sharp.PdfInteger(46), new Sharp.PdfName("/period"));
        var font = new Sharp.PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type3");
        font.Elements["/FontBBox"] = Numbers(document, 0, 0, 1000, 1000);
        font.Elements["/FontMatrix"] = Numbers(document, 0.001, 0, 0, 0.001, 0, 0);
        font.Elements["/CharProcs"] = procedures;
        font.Elements["/Encoding"] = encoding;
        font.Elements.SetInteger("/FirstChar", 46);
        font.Elements.SetInteger("/LastChar", 46);
        font.Elements["/Widths"] = Numbers(document, 1000);
        font.Elements["/Resources"] = new Sharp.PdfDictionary(document);
        return font;
    }

    /// <summary>
    /// Arial (a TrueType font, not embedded) whose code 1 is a ligature, 800/1000 em wide, with no glyph name: its text
    /// is "ffi" by ToUnicode only.
    /// </summary>
    private static Sharp.PdfDictionary LigatureFont(Sharp.PdfDocument document)
    {
        var text = new Sharp.PdfDictionary(document);
        text.CreateStream(Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Lg def 1 begincodespacerange <00> <FF> endcodespacerange " +
            "1 beginbfchar <01> <006600660069> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end"));
        document.Internals.AddObject(text);
        var descriptor = new Sharp.PdfDictionary(document);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/Arial");
        descriptor.Elements.SetInteger("/Flags", 32);
        descriptor.Elements["/FontBBox"] = Numbers(document, -665, -325, 2000, 1040);
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", 905);
        descriptor.Elements.SetInteger("/Descent", -212);
        descriptor.Elements.SetInteger("/CapHeight", 716);
        descriptor.Elements.SetInteger("/StemV", 80);
        document.Internals.AddObject(descriptor);
        var font = new Sharp.PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements.SetName("/BaseFont", "/Arial");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        font.Elements.SetInteger("/FirstChar", 1);
        font.Elements.SetInteger("/LastChar", 1);
        font.Elements["/Widths"] = Numbers(document, 800);
        font.Elements.SetReference("/FontDescriptor", descriptor);
        font.Elements.SetReference("/ToUnicode", text);
        return font;
    }

    /// <summary>
    /// The backdrop of a blend has what PdfPig alone can't draw: a JBIG2 picture (Filee's decoder), a Type 3 glyph
    /// (its procedure, not its text) and a ligature (all its letters, not the first).
    /// </summary>
    [Fact]
    public async Task Backdrops_have_jbig2_pictures_type_3_glyphs_and_whole_ligatures()
    {
        var pptx = await ConvertAsync(MakeBackdropsPdf(fx.NewFolder()));
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)x, (int)(Height - y));
        static bool Blue(SKColor c) => c is { Red: < 60, Green: < 60, Blue: > 200 };
        static bool White(SKColor c) => c is { Red: > 220, Green: > 220, Blue: > 220 };

        // JBIG2: black screened blue is blue, white stays white.
        Assert.True(Blue(At(100, 250)), At(100, 250).ToString());
        Assert.True(White(At(220, 250)), At(220, 250).ToString());
        // Type 3: the red square (400–430, 250–280) screened blue is magenta all over, not a red dot.
        var magenta = 0;
        for (var x = 402; x < 428; x++)
        {
            for (var y = 252; y < 278; y++)
                magenta += At(x, y) is { Red: > 200, Green: < 60, Blue: > 200 } ? 1 : 0;
        }
        Assert.True(magenta > 26 * 26 * 0.9, $"{magenta} magenta pixels");
        // The ligature: black screened blue across most of its 48 pt, not just the width of an "f".
        var (left, right) = (int.MaxValue, int.MinValue);
        for (var x = 590; x < 680; x++)
        {
            for (var y = 252; y < 290; y++)
            {
                if (Blue(At(x, y)))
                    (left, right) = (Math.Min(left, x), Math.Max(right, x));
            }
        }
        Assert.True(right - left > 30, $"ligature ink from {left} to {right}");
    }

    private static Sharp.PdfDictionary RgbPicture(Sharp.PdfDocument document, int width, int height, Func<int, (int R, int G, int B)> pixel)
    {
        var picture = new Sharp.PdfDictionary(document);
        picture.Elements.SetName("/Type", "/XObject");
        picture.Elements.SetName("/Subtype", "/Image");
        picture.Elements.SetInteger("/Width", width);
        picture.Elements.SetInteger("/Height", height);
        picture.Elements.SetInteger("/BitsPerComponent", 8);
        picture.Elements.SetName("/ColorSpace", "/DeviceRGB");
        picture.CreateStream([.. Enumerable.Range(0, width * height).Select(pixel).SelectMany(c => new[] { (byte)c.R, (byte)c.G, (byte)c.B })]);
        return picture;
    }

    private static Sharp.PdfDictionary BlendState(Sharp.PdfDocument document, string mode)
    {
        var blend = new Sharp.PdfDictionary(document);
        blend.Elements.SetName("/Type", "/ExtGState");
        blend.Elements.SetName("/BM", mode);
        return blend;
    }

    [Fact]
    public async Task Screen_and_overlay_blend_with_the_pictures_and_text_under_them()
    {
        var pptx = await ConvertAsync(MakeBlendPdf(fx.NewFolder()));
        var (tree, _) = Assert.Single(Slides(pptx));
        // The text stays a text box, under the picture of the blend.
        Assert.Contains("Backdrop", TextBox(tree, "Backdrop").Value, StringComparison.Ordinal);
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)x, (int)(Height - y));
        static void Near((int R, int G, int B) expected, SKColor actual) =>
            Assert.True(Math.Abs(actual.Red - expected.R) <= 30 && Math.Abs(actual.Green - expected.G) <= 30 && Math.Abs(actual.Blue - expected.B) <= 30,
                $"{expected} vs {actual}");

        // Screen with blue over the picture: its red and green, full blue (blue alone before).
        var (red, green, _) = Photo(382, 362);
        Near((red, green, 255), At(382, 362));
        // Overlay with orange over the picture's darker half: twice the picture times orange (orange alone before).
        var dark = Photo(202, 362);
        Near((2 * dark.R, 2 * dark.G * 128 / 255, 0), At(202, 362));
        // Screen over the text: white between the letters, blue on them; white over the bare page.
        var (white, blue) = (0, 0);
        for (var x = 302; x < 350; x++)
        {
            for (var y = 114; y < 146; y++)
            {
                var pixel = At(x, y);
                white += pixel is { Red: > 230, Green: > 230, Blue: > 230 } ? 1 : 0;
                blue += pixel is { Red: < 60, Green: < 60, Blue: > 200 } ? 1 : 0;
            }
        }
        Assert.True(white > 100 && blue > 100, $"white {white}, blue {blue}");
        Near((255, 255, 255), At(360, 220));
        // The dark blue picture screened over red: red and half blue (the picture alone before).
        Near((255, 0, 128), At(700, 350));
    }

    /// <summary>
    /// A luminosity mask with an inverting transfer function (white left half: hidden; black elsewhere: shown), and one
    /// with a white backdrop BC whose group covers only the left half (black there: hidden; outside: shown).
    /// </summary>
    [Fact]
    public async Task Soft_mask_transfer_functions_and_backdrops_outside_the_group_are_applied()
    {
        var content = "q /GS1 gs 1 0 0 rg 0 50 960 50 re f Q\nq /GS2 gs 0 0 1 rg 0 150 960 50 re f Q\n";
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "transfer.pdf"), content, (document, page) =>
        {
            var inverted = MaskState(document, "1 g 0 0 480 540 re f");
            var function = new Sharp.PdfDictionary(document);
            function.Elements.SetInteger("/FunctionType", 2);
            function.Elements["/Domain"] = Numbers(document, 0, 1);
            function.Elements["/C0"] = Numbers(document, 1);
            function.Elements["/C1"] = Numbers(document, 0);
            function.Elements.SetInteger("/N", 1);
            document.Internals.AddObject(function);
            inverted.Elements.GetDictionary("/SMask")!.Elements.SetReference("/TR", function);
            Resource(document, page, "/ExtGState", "/GS1", inverted);

            var backdrop = MaskState(document, "0 g 0 0 480 540 re f");
            var mask = backdrop.Elements.GetDictionary("/SMask")!;
            mask.Elements["/BC"] = Numbers(document, 1, 1, 1);
            mask.Elements.GetDictionary("/G")!.Elements["/BBox"] = Numbers(document, 0, 0, 480, 540);
            Resource(document, page, "/ExtGState", "/GS2", backdrop);
        });

        var pptx = await ConvertAsync(pdf);
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        SKColor At(double x, double y) => bitmap.GetPixel((int)x, (int)(Height - y));
        Assert.True(At(200, 75) is { Red: > 230, Green: > 230, Blue: > 230 }, At(200, 75).ToString());
        Assert.True(At(700, 75) is { Red: > 230, Green: < 40, Blue: < 40 }, At(700, 75).ToString());
        Assert.True(At(200, 175) is { Red: > 230, Green: > 230, Blue: > 230 }, At(200, 175).ToString());
        Assert.True(At(700, 175) is { Red: < 40, Green: < 40, Blue: > 230 }, At(700, 175).ToString());
    }

    // ───────────────────────── Hostile pages ─────────────────────────

    [Fact]
    public async Task Thousands_of_blends_over_a_picture_and_text_finish_quickly()
    {
        var content = new StringBuilder("q 960 0 0 540 0 0 cm /Ph Do Q\nBT /F1 10 Tf\n");
        for (var i = 0; i < 400; i++)
            content.Append(Inv($"1 0 0 1 {i % 20 * 48} {i / 20 * 27} Tm (backdrop {i}) Tj\n"));
        content.Append("ET\n");
        for (var i = 0; i < 3000; i++)
            content.Append(Inv($"q /GS1 gs 0 0 1 rg {i % 60 * 16} {i / 60 * 10} 14 9 re f Q\n"));
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "blends.pdf"), content.ToString(), (document, page) =>
        {
            Resource(document, page, "/XObject", "/Ph", RgbPicture(document, 256, 144, i => (i % 256, i / 256, 128)));
            Resource(document, page, "/ExtGState", "/GS1", BlendState(document, "/Screen"));
        });

        var stopwatch = Stopwatch.StartNew();
        var pptx = await ConvertAsync(pdf);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
        Assert.Single(Slides(pptx));
        // A late blend (the 2,880th) still sees the picture under it: its red and green, full blue (white before).
        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        var pixel = bitmap.GetPixel(951, (int)(Height - 475));
        Assert.True(Math.Abs(pixel.Red - 253) <= 30 && Math.Abs(pixel.Green - 17) <= 30 && pixel.Blue > 225, pixel.ToString());
    }

    /// <summary>
    /// Type 3 glyphs that are expensive or odd: a procedure of 2,000 rectangles shown 5,000 times (drawn once), one
    /// that shows its own font's glyph (not followed into), 300 fonts of their own (past the procedures drawn a page),
    /// all under a blend over them.
    /// </summary>
    [Fact]
    public async Task Type_3_glyphs_that_paint_much_or_show_themselves_finish_quickly()
    {
        var heavy = new StringBuilder("1000 0 0 0 1000 1000 d1 ");
        for (var i = 0; i < 2000; i++)
            heavy.Append(Inv($"{i % 50 * 20} {i / 50 * 25} 10 10 re f "));
        var content = new StringBuilder("BT /H 8 Tf\n");
        for (var i = 0; i < 5000; i++)
            content.Append(Inv($"1 0 0 1 {i % 100 * 9.6} {i / 100 * 10.8} Tm (.) Tj\n"));
        content.Append("ET BT /S 20 Tf 100 100 Td (.) Tj ET\nBT\n");
        for (var i = 0; i < 300; i++)
            content.Append(Inv($"/F{i} 12 Tf 1 0 0 1 {i % 30 * 32} {200 + i / 30 * 30} Tm (.) Tj\n"));
        content.Append("ET q /GS1 gs 0 0 1 rg 0 0 960 540 re f Q\n");
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "type3.pdf"), content.ToString(), (document, page) =>
        {
            Resource(document, page, "/Font", "/H", Type3Font(document, heavy.ToString()));
            var self = Type3Font(document, "1000 0 0 0 1000 1000 d0 BT /S 500 Tf (.) Tj ET 0 0 500 500 re f");
            self.Elements.GetDictionary("/Resources")!.Elements["/Font"] = new Sharp.PdfDictionary(document);
            Resource(document, page, "/Font", "/S", self);
            self.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements.SetReference("/S", self);
            for (var i = 0; i < 300; i++)
                Resource(document, page, "/Font", $"/F{i}", Type3Font(document, Inv($"1000 0 0 0 1000 1000 d1 0 0 {i + 100} 600 re f")));
            Resource(document, page, "/ExtGState", "/GS1", BlendState(document, "/Multiply"));
        });

        var stopwatch = Stopwatch.StartNew();
        var pptx = await ConvertAsync(pdf);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
        Assert.Single(Slides(pptx));
    }

    [Fact]
    public async Task Huge_meshes_functions_and_drawn_masks_finish_quickly()
    {
        var content = new StringBuilder("q /Sh1 sh Q q /Sh2 sh Q\n");
        // 400 soft masks of drawn groups (past the mask budget) over small bars.
        for (var i = 0; i < 400; i++)
            content.Append(Inv($"q /M{i} gs 1 0 0 rg {i % 40 * 24} {i / 40 * 54} 20 50 re f Q\n"));
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "meshes.pdf"), content.ToString(), (document, page) =>
        {
            // 300,000 triangles in one strip, more than MaxTriangles.
            var strip = new List<byte>();
            for (var i = 0; i < 300_002; i++)
                strip.AddRange([(byte)(i < 3 ? 0 : 1), .. MeshPoint(i % 960, i % 2 * 540, ((byte)i, 0, 0))]);
            Resource(document, page, "/Shading", "/Sh1", Mesh(document, 4, [.. strip]));
            // A function-based shading over a huge domain.
            var huge = FunctionShading(document, [1e6, 0, 0, 1e6, -5e5, -5e5], (0, 0, 0), (255, 255, 255), (0, 0, 0), (255, 255, 255));
            Resource(document, page, "/Shading", "/Sh2", huge);
            for (var i = 0; i < 400; i++)
                Resource(document, page, "/ExtGState", $"/M{i}", MaskState(document, Inv($"1 g 0 0 {Width} {Height} re f 0.5 g 0 0 {i + 1} 10 re f")));
        });

        var stopwatch = Stopwatch.StartNew();
        var pptx = await ConvertAsync(pdf);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
        Assert.Single(Slides(pptx));
    }

    [Fact]
    public async Task Deep_clips_huge_tiles_and_nested_patterns_finish_quickly()
    {
        var content = new StringBuilder();
        // 5,000 nested clips (each a little smaller), then a fill through all of them.
        for (var i = 0; i < 5000; i++)
            content.Append(Inv($"q {i * 0.05} {i * 0.05} {960 - i * 0.1} {540 - i * 0.1} re W n "));
        content.Append("0 0 1 rg 0 0 960 540 re f ");
        for (var i = 0; i < 5000; i++)
            content.Append("Q ");
        // A pattern whose cell holds itself, a tile with a huge step, many turned words.
        content.Append("q /Pattern cs /T1 scn 0 0 100 100 re f /Pattern cs /T2 scn 100 0 100 100 re f Q\n");
        for (var i = 0; i < 2000; i++)
            content.Append(Inv($"BT /F1 6 Tf 0 1 -1 0 {10 + i % 90 * 10} {10 + i / 90 * 20} Tm (w{i}) Tj ET\n"));
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "hostile.pdf"), content.ToString(), (document, page) =>
        {
            var self = new Sharp.PdfDictionary(document);
            self.Elements.SetName("/Type", "/Pattern");
            self.Elements.SetInteger("/PatternType", 1);
            self.Elements.SetInteger("/PaintType", 1);
            self.Elements.SetInteger("/TilingType", 1);
            self.Elements["/BBox"] = Numbers(document, 0, 0, 10, 10);
            self.Elements.SetInteger("/XStep", 10);
            self.Elements.SetInteger("/YStep", 10);
            self.CreateStream("/Pattern cs /T1 scn 0 0 10 10 re f"u8.ToArray());
            document.Internals.AddObject(self);
            var selfResources = new Sharp.PdfDictionary(document);
            var selfPatterns = new Sharp.PdfDictionary(document);
            selfPatterns.Elements.SetReference("/T1", self);
            selfResources.Elements["/Pattern"] = selfPatterns;
            self.Elements["/Resources"] = selfResources;
            Resource(document, page, "/Pattern", "/T1", self);
            var huge = new Sharp.PdfDictionary(document);
            huge.Elements.SetName("/Type", "/Pattern");
            huge.Elements.SetInteger("/PatternType", 1);
            huge.Elements.SetInteger("/PaintType", 1);
            huge.Elements.SetInteger("/TilingType", 1);
            huge.Elements["/BBox"] = Numbers(document, 0, 0, 1e6, 1e6);
            huge.Elements["/XStep"] = new Sharp.PdfReal(1e6);
            huge.Elements["/YStep"] = new Sharp.PdfReal(1e-9);
            huge.Elements["/Resources"] = new Sharp.PdfDictionary(document);
            huge.CreateStream("1 0 0 rg 0 0 5 5 re f"u8.ToArray());
            Resource(document, page, "/Pattern", "/T2", huge);
        });

        var stopwatch = Stopwatch.StartNew();
        var pptx = await ConvertAsync(pdf);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
        var (tree, _) = Assert.Single(Slides(pptx));
        Assert.Contains(tree.Descendants(P + "sp"), s => Attribute(s, "xfrm", "rot") == "16200000" && Text(s).StartsWith('w'));
    }
}
