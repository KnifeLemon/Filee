// The PowerPoint 97–2003 reader (Office/Ppt): decks built in code (PptBuilder) are translated to PPTX and read back
// through the slide renderer (PptxSlideReader, SlideOutput) and the document reader (PptxReader), converted through the
// engines (PPT → PDF with the Filee renderer, PPT → PPTX, PPT → DOCX), and refused or bounded where they should be.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Ppt;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.PptBuilder;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the conversion test turns the Filee renderer on, which is process-wide
public sealed class PptReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    /// <summary>An OfficeArtCOLORREF of an RRGGBB colour (red in the low byte).</summary>
    internal static uint Rgb(uint rrggbb) => (rrggbb >> 16 & 0xFF) | (rrggbb & 0xFF00) | (rrggbb & 0xFF) << 16;

    private static readonly (int, uint)[] NoFillNoLine = [(0x01BF, 0x00100000), (0x01FF, 0x00080000)];

    /// <summary>
    /// A master with a band, title and body placeholders; a title master with a band; a slide with placeholders whose
    /// text is in the outline, a text box, an ellipse, an arrow, a picture, a group and notes; a title slide with its own
    /// colour scheme and background; a hidden slide.
    /// </summary>
    internal static PptBuilder RichDeck()
    {
        var ppt = new PptBuilder();
        ppt.Pictures.Add(DocumentFuzzTests.TinyPng(4, 3));
        ppt.Master.Background = [(0x0181, 0x08000000), (0x01BF, 0x00100010)];
        ppt.Master.Shapes.Add(Shape(2001, 1, (0, 0, 5760, 288), [(0x0181, Rgb(0x003399)), (0x01BF, 0x00100010), (0x01FF, 0x00080000)]));
        ppt.Master.Shapes.Add(Shape(2002, 1, (288, 360, 5472, 1080), [.. NoFillNoLine, (0x0087, 1)], Placeholder(1), TextBox(0, "Click to edit Master title style")));
        ppt.Master.Shapes.Add(Shape(2003, 1, (288, 1260, 5472, 4032), NoFillNoLine, Placeholder(2, 1), TextBox(1, "Click to edit Master text styles")));

        ppt.TitleMaster = new Sheet { Flags = 0x2 | 0x4 }; // without the main master's objects
        ppt.TitleMaster.Shapes.Add(Shape(2101, 1, (0, 3960, 5760, 4320), [(0x0181, Rgb(0x00CCFF)), (0x01BF, 0x00100010), (0x01FF, 0x00080000)]));
        ppt.TitleMaster.Shapes.Add(Shape(2102, 1, (432, 1340, 5328, 2260), NoFillNoLine, Placeholder(3), TextBox(6, "Click to edit Master title style")));
        ppt.TitleMaster.Shapes.Add(Shape(2103, 1, (864, 2448, 4896, 3528), NoFillNoLine, Placeholder(4, 1), TextBox(5, "Click to edit Master subtitle style")));

        var slide = new Sheet { Notes = "Speaker note" };
        slide.Outline.AddRange(TextRecords(0, "Hello PPT"));
        slide.Outline.AddRange(TextRecords(1, "First point\rSecond point",
            Styles([(12, 0, Paragraph()), (13, 1, Paragraph())], [(5, Character(bold: true, color: 0xC00000)), (20, Character())])));
        // The title takes what it doesn't set (no fill, no line, centred text) from the master's title.
        slide.Shapes.Add(Shape(3001, 1, (288, 360, 5472, 1080), [(0x0301, 2002)], Placeholder(13), OutlineRef(0), flags: 0x20));
        slide.Shapes.Add(Shape(3002, 1, (288, 1260, 5472, 2880), NoFillNoLine, Placeholder(14, 1), OutlineRef(1)));
        slide.Shapes.Add(Shape(3003, 202, (288, 3600, 2880, 3960), NoFillNoLine, null,
            TextBox(4, "Plain text box", Styles([(15, 0, Paragraph(align: 2))], [(15, Character(italic: true, size: 20))]), bytes: true)));
        slide.Shapes.Add(Shape(3004, 3, (3600, 2880, 4320, 3600), [(0x0181, Rgb(0xFF0000)), (0x01BF, 0x00100010), (0x01C0, Rgb(0x00FF00)), (0x01CB, 25400)]));
        slide.Shapes.Add(Shape(3005, 20, (288, 4100, 2880, 4101), [(0x01C0, 0), (0x01D1, 1)]));
        slide.Shapes.Add(Shape(3006, 75, (4400, 2880, 5400, 3600), [(0x0104, 1)]));
        slide.Shapes.Add(Group(3007, (3600, 3700, 5400, 4100), (0, 0, 1000, 1000), false,
            Shape(3008, 1, (0, 0, 500, 1000), [(0x0181, Rgb(0x0000FF)), (0x01BF, 0x00100010)], child: true),
            Shape(3009, 1, (500, 0, 1000, 1000), [(0x0181, Rgb(0xFFFF00)), (0x01BF, 0x00100010)], child: true)));
        ppt.Slides.Add(slide);

        // Its own scheme (dark background, yellow title) and background; the title master's objects.
        var title = new Sheet
        {
            OnTitleMaster = true,
            Flags = 0x1,
            Scheme = [0x202020, 0xFFFFFF, 0x000000, 0xFFCC00, 0x336699, 0x99CCFF, 0xCCCCFF, 0xB2B2B2],
            Background = [(0x0181, 0x08000000), (0x01BF, 0x00100010)],
        };
        title.Outline.AddRange(TextRecords(6, "Title slide"));
        title.Outline.AddRange(TextRecords(5, "Subtitle here"));
        title.Shapes.Add(Shape(3101, 1, (432, 1340, 5328, 2260), NoFillNoLine, Placeholder(15), OutlineRef(0)));
        title.Shapes.Add(Shape(3102, 1, (864, 2448, 4896, 3528), NoFillNoLine, Placeholder(16, 1), OutlineRef(1)));
        ppt.Slides.Add(title);

        var hidden = new Sheet { Hidden = true };
        hidden.Shapes.Add(Shape(3201, 202, (288, 288, 2880, 720), NoFillNoLine, null, TextBox(4, "Hidden")));
        ppt.Slides.Add(hidden);
        return ppt;
    }

    private string Save(PptBuilder builder, string name = "deck.ppt")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static string TextOf(SlideShape shape) =>
        string.Concat(shape.Text?.Paragraphs.SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text) ?? []);

    private static IEnumerable<SlideElement> All(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? [e, .. All(group.Children)] : new[] { e });

    // ───────────────────────── The package ─────────────────────────

    [Fact]
    public void The_translated_package_is_valid_with_masters_layouts_slides_and_notes()
    {
        var path = Save(RichDeck());
        var pptx = Path.Combine(fx.NewFolder(), "deck.pptx");
        PptReader.ToPptx(path, pptx, cancellationToken: Token);
        PptxWriterTests.Valid(pptx);

        using var zip = ZipFile.OpenRead(pptx);
        XElement Part(string name) => XDocument.Load(zip.GetEntry(name)!.Open()).Root!;
        var presentation = Part("ppt/presentation.xml");
        Assert.Equal(3, presentation.Element(P + "sldIdLst")!.Elements().Count());
        // 10 × 7.5 inches.
        Assert.Equal("9144000", (string?)presentation.Element(P + "sldSz")!.Attribute("cx"));
        Assert.Equal("6858000", (string?)presentation.Element(P + "sldSz")!.Attribute("cy"));
        // The title slide's own colour scheme is a copy of the master with that scheme in its theme.
        Assert.Equal(2, presentation.Element(P + "sldMasterIdLst")!.Elements().Count());
        Assert.Contains("FFCC00", zip.Entries.Where(e => e.FullName.StartsWith("ppt/theme/", StringComparison.Ordinal))
            .Select(e => new StreamReader(e.Open()).ReadToEnd()).Single(t => t.Contains("202020", StringComparison.Ordinal)));

        var first = Part("ppt/slides/slide1.xml");
        var title = first.Descendants(P + "sp").First();
        Assert.Equal("title", (string?)title.Descendants(P + "ph").Single().Attribute("type"));
        Assert.Equal("Hello PPT", string.Concat(title.Descendants(A + "t").Select(t => t.Value)));
        // The master's title: no fill, no line, middle anchor.
        Assert.NotNull(title.Element(P + "spPr")!.Element(A + "noFill"));
        Assert.Equal("ctr", (string?)title.Descendants(A + "bodyPr").Single().Attribute("anchor"));
        Assert.Single(first.Descendants(P + "pic"));
        Assert.Single(first.Descendants(P + "grpSp"));
        Assert.Equal("triangle", (string?)first.Descendants(A + "tailEnd").Single().Attribute("type"));
        Assert.Contains(zip.Entries, e => e.FullName == "ppt/media/image1.png");
        Assert.Contains("Speaker note", new StreamReader(zip.GetEntry("ppt/notesSlides/notesSlide1.xml")!.Open()).ReadToEnd());

        var second = Part("ppt/slides/slide2.xml");
        Assert.Equal("ctrTitle", (string?)second.Descendants(P + "ph").First().Attribute("type"));
        Assert.NotNull(second.Descendants(P + "bg").SingleOrDefault());
        Assert.Equal("0", (string?)Part("ppt/slides/slide3.xml").Attribute("show"));
        var layout = Part("ppt/slideLayouts/slideLayout" + RelationshipTarget(zip, "ppt/slides/_rels/slide2.xml.rels", "slideLayout")[^5] + ".xml");
        Assert.Equal("title", (string?)layout.Attribute("type"));
        // The master's text styles.
        var master = Part("ppt/slideMasters/slideMaster1.xml");
        var titleStyle = master.Descendants(P + "titleStyle").Single().Element(A + "lvl1pPr")!;
        Assert.Equal("ctr", (string?)titleStyle.Attribute("algn"));
        Assert.Equal("4400", (string?)titleStyle.Element(A + "defRPr")!.Attribute("sz"));
        Assert.Equal(5, master.Descendants(P + "bodyStyle").Single().Elements().Count());
    }

    private static string RelationshipTarget(ZipArchive zip, string rels, string type)
    {
        XNamespace r = "http://schemas.openxmlformats.org/package/2006/relationships";
        return (string)XDocument.Load(zip.GetEntry(rels)!.Open()).Root!.Elements(r + "Relationship")
            .Single(e => ((string)e.Attribute("Type")!).EndsWith("/" + type, StringComparison.Ordinal)).Attribute("Target")!;
    }

    // ───────────────────────── Slide renderer ─────────────────────────

    [Fact]
    public void The_slide_renderer_reads_text_styles_shapes_pictures_and_groups()
    {
        var deck = PptxSlideReader.Read(Save(RichDeck()), cancellationToken: Token);
        Assert.Equal(720, deck.Width, 0.01);
        Assert.Equal(540, deck.Height, 0.01);
        Assert.Equal(2, deck.Slides.Count); // the hidden slide is left out

        var elements = All(deck.Slides[0].Elements).ToList();
        var shapes = elements.OfType<SlideShape>().ToList();
        var title = shapes.Single(s => TextOf(s) == "Hello PPT");
        Assert.Equal(new SKRect(36, 45, 684, 135), title.Bounds);
        Assert.Null(title.Fill);
        var heading = title.Text!.Paragraphs[0].Inlines.OfType<HText>().Single();
        Assert.Equal(4400, heading.Format.Size);
        Assert.Equal("Arial", heading.Format.Font);
        Assert.Equal(TextAnchor.Middle, title.Text.Anchor);

        var body = shapes.Single(s => TextOf(s).Contains("First point", StringComparison.Ordinal));
        var first = body.Text!.Paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.Contains(first, t => t.Text.Contains("First", StringComparison.Ordinal) && t.Format.Bold == true);
        Assert.Contains(first, t => t.Text == "•"); // the master's bullet
        Assert.Equal(3200, first.Last().Format.Size);
        Assert.Equal(2800, body.Text.Paragraphs[1].Inlines.OfType<HText>().Last().Format.Size); // level 2

        var box = shapes.Single(s => TextOf(s) == "Plain text box");
        Assert.Equal(true, box.Text!.Paragraphs[0].Inlines.OfType<HText>().Single().Format.Italic);

        var ellipse = shapes.Single(s => s.Fill is SolidFill { Color: var c } && c == new SKColor(0xFF, 0, 0));
        Assert.NotNull(ellipse.Line);
        Assert.Single(shapes, s => s.IsPicture);
        var group = Assert.Single(deck.Slides[0].Elements.OfType<SlideGroup>());
        Assert.Equal(2, group.Children.Count);
        // The master's band is drawn under the slide's shapes.
        Assert.Contains(shapes, s => s.Fill is SolidFill { Color: var c } && c == new SKColor(0x00, 0x33, 0x99));

        // The title slide: its own scheme's title colour through the master's style, and the title master's band.
        var titleSlide = All(deck.Slides[1].Elements).OfType<SlideShape>().ToList();
        Assert.Contains(titleSlide, s => s.Fill is SolidFill { Color: var c } && c == new SKColor(0x00, 0xCC, 0xFF));
        Assert.DoesNotContain(titleSlide, s => s.Fill is SolidFill { Color: var c } && c == new SKColor(0x00, 0x33, 0x99));
        var big = titleSlide.Single(s => TextOf(s) == "Title slide").Text!.Paragraphs[0].Inlines.OfType<HText>().Single();
        Assert.Equal(4000, big.Format.Size); // the centred title style
        Assert.Equal("FFCC00", big.Format.Color?.TrimStart('#'), ignoreCase: true);
        Assert.Equal(new SKColor(0x20, 0x20, 0x20), Assert.IsType<SolidFill>(deck.Slides[1].Background).Color);
    }

    [Fact]
    public void The_slides_are_drawn_with_their_colours()
    {
        var deck = PptxSlideReader.Read(Save(RichDeck()), cancellationToken: Token);
        var pictures = SlideOutput.ToBitmaps(deck, 72, [0, 1], Token).ToList();
        try
        {
            var slide = pictures[0];
            Assert.Equal(720, slide.Width);
            Assert.Equal(new SKColor(0xFF, 0, 0), slide.GetPixel(495, 405)); // the ellipse
            Assert.Equal(new SKColor(0x00, 0x33, 0x99), slide.GetPixel(360, 18)); // the master's band
            Assert.Equal(SKColors.White, slide.GetPixel(700, 300)); // the master's background
            Assert.Equal(new SKColor(0, 0, 0xFF), slide.GetPixel(470, 487)); // the group's first rectangle
            var title = pictures[1];
            Assert.Equal(new SKColor(0x20, 0x20, 0x20), title.GetPixel(10, 270));
            Assert.Equal(new SKColor(0x00, 0xCC, 0xFF), title.GetPixel(360, 530));
        }
        finally
        {
            foreach (var picture in pictures)
                picture.Dispose();
        }
    }

    // ───────────────────────── Document readers and conversions ─────────────────────────

    [Fact]
    public async Task The_document_reader_reads_every_slide_s_text()
    {
        var document = await DocumentReaders.ReadAsync(Save(RichDeck()), "ppt", fx.NewFolder(), Token);
        var text = string.Concat(document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).OfType<HText>().Select(t => t.Text));
        Assert.Contains("Hello PPT", text, StringComparison.Ordinal);
        Assert.Contains("Second point", text, StringComparison.Ordinal);
        Assert.Contains("Plain text box", text, StringComparison.Ordinal);
        Assert.Contains("Subtitle here", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ppt_converts_to_pdf_with_the_renderer_and_to_pptx_and_docx_without_libreoffice()
    {
        EngineEnvironment.FileeRenderer = true;
        fx.Catalog.Refresh();
        var source = Save(RichDeck());
        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal("layout", Assert.Single(planner.Plan("ppt", "pdf")!.Steps).Converter.Id);
        Assert.Equal("pptx-writer", Assert.Single(planner.Plan("ppt", "pptx")!.Steps).Converter.Id);

        var pdfJob = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, pdfJob.State);
        using (var pdf = PigDocument.Open(pdfJob.Outputs.Single()))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Equal(720, pdf.GetPage(1).Width, 1);
            Assert.Contains("Hello PPT", pdf.GetPage(1).Text, StringComparison.Ordinal);
        }

        var pptxJob = await fx.ConvertAsync([source], new Preset { TargetFormat = "pptx" });
        Assert.Equal(JobState.Completed, pptxJob.State);
        PptxWriterTests.Valid(pptxJob.Outputs.Single());

        var docxJob = await fx.ConvertAsync([source], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, docxJob.State);
    }

    [Fact]
    public void Every_shape_type_becomes_a_preset_the_slide_renderer_draws()
    {
        Assert.All(PptToPptx.Presets, p => Assert.True(PresetShapes.Known(p.Value), $"MSOSPT {p.Key}: {p.Value}"));

        // MSOSPT 106 is DrawingML's cloudCallout: the check passes it and the renderer fills its cloud.
        var ppt = new PptBuilder();
        var slide = new Sheet();
        slide.Shapes.Add(Shape(3001, 106, (288, 288, 2880, 2880), [(0x0181, Rgb(0xFF0000)), (0x01BF, 0x00100010)]));
        ppt.Slides.Add(slide);
        var path = Save(ppt);
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "ppt"));
        using var picture = SlideOutput.ToBitmaps(PptxSlideReader.Read(path, cancellationToken: Token), 72, [0], Token).Single();
        Assert.Equal(new SKColor(0xFF, 0, 0), picture.GetPixel(216, 216));
    }

    // ───────────────────────── Refused and left out ─────────────────────────

    [Fact]
    public void Encrypted_and_powerpoint_95_files_are_refused_with_what_to_do()
    {
        var encrypted = Save(new PptBuilder { Encrypted = true }, "locked.ppt");
        var ex = Assert.Throws<InvalidDataException>(() => PptxSlideReader.Read(encrypted, cancellationToken: Token));
        Assert.Contains("password", ex.Message, StringComparison.Ordinal);
        Assert.Null(PptReader.Unsupported(encrypted));

        var old = Save(new PptBuilder { PowerPoint95 = true }, "old.ppt");
        Assert.Contains("LibreOffice", Assert.Throws<InvalidDataException>(() => PptxSlideReader.Read(old, cancellationToken: Token)).Message, StringComparison.Ordinal);
        Assert.Equal("a PowerPoint 95 or older presentation", PptReader.Unsupported(old));
        Assert.Equal("a PowerPoint 95 or older presentation", new FileeRendererConverter().FirstUnsupported(old, "ppt"));
        Assert.Equal("a PowerPoint 95 or older presentation", DocumentReaders.Unsupported(old, "ppt"));
        Assert.Null(PptReader.Unsupported(Save(RichDeck())));
    }

    [Fact]
    public void What_the_translation_leaves_out_is_named_for_the_capability_check()
    {
        var ppt = new PptBuilder();
        var slide = new Sheet();
        slide.Shapes.Add(Shape(3001, 0, (288, 288, 2880, 2880), [(0x0181, Rgb(0xFF0000))]));
        ppt.Slides.Add(slide);
        Assert.Equal("freeform shapes", new FileeRendererConverter().FirstUnsupported(Save(ppt), "ppt"));
        Assert.Null(new FileeRendererConverter().FirstUnsupported(Save(RichDeck()), "ppt"));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_user_edit_that_points_to_itself_is_read_once()
    {
        var deck = PptxSlideReader.Read(Save(new PptBuilder { EditLoop = true, Slides = { new Sheet() } }), cancellationToken: Token);
        Assert.Single(deck.Slides);
    }

    [Fact]
    public void Groups_nested_past_the_limit_are_refused()
    {
        var shape = Shape(9000, 1, (0, 0, 10, 10), [(0x0181, Rgb(0xFF0000))], child: true);
        for (var i = 0; i < 40; i++)
            shape = Group(9001 + i, (0, 0, 10, 10), (0, 0, 10, 10), i < 39, shape);
        var ppt = new PptBuilder();
        var slide = new Sheet();
        slide.Shapes.Add(shape);
        ppt.Slides.Add(slide);
        var ex = Assert.Throws<ConversionRefusedException>(() => PptxSlideReader.Read(Save(ppt), cancellationToken: Token));
        Assert.Equal(DocumentLimits.ErrorKey, ex.ErrorKey);
    }

    [Fact]
    public void Shapes_past_the_object_limit_are_refused()
    {
        var ppt = new PptBuilder();
        var slide = new Sheet();
        for (var i = 0; i < 50; i++)
            slide.Shapes.Add(Shape(4000 + i, 1, (0, 0, 10, 10), [(0x0181, Rgb(0xFF0000))]));
        ppt.Slides.Add(slide);
        var limits = DocumentLimits.Default with { MaxObjects = 20 };
        var ex = Assert.Throws<ConversionRefusedException>(() => PptReader.ToPptx(Save(ppt), limits, out _, Token).Dispose());
        Assert.Equal(DocumentLimits.ErrorKey, ex.ErrorKey);
    }

    [Fact]
    public void Run_counts_and_text_lengths_from_the_file_stay_inside_their_records()
    {
        var ppt = new PptBuilder();
        var slide = new Sheet();
        // Runs that claim to cover four billion characters, a paragraph format cut short, a text box without text.
        var styles = Atom(0x0FA1, Bytes(uint.MaxValue, (ushort)9, 0x00FFFFFFu, (ushort)1));
        slide.Shapes.Add(Shape(3001, 202, (288, 288, 2880, 720), NoFillNoLine, null, TextBox(4, "Runs\rrun\vover", styles)));
        slide.Shapes.Add(Shape(3002, 202, (288, 800, 2880, 1200), NoFillNoLine, null, Container(0xF00D, 0, Atom(0x0F9F, Bytes(4)))));
        slide.Shapes.Add(Shape(3003, 1, (288, 1300, 2880, 1600), NoFillNoLine, Placeholder(14), OutlineRef(99)));
        ppt.Slides.Add(slide);
        var deck = PptxSlideReader.Read(Save(ppt), cancellationToken: Token);
        Assert.Contains(deck.Slides[0].Elements.OfType<SlideShape>(), s => TextOf(s).Contains("Runs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_picture_that_unpacks_far_beyond_its_size_is_refused()
    {
        // An EMF BLIP whose deflated data unpacks to 64 MB of zeros.
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(new byte[64 * 1024 * 1024]);
        var header = Bytes(64 * 1024 * 1024, 0, 0, 100, 100, 914400, 914400, (int)packed.Length, (byte)0, (byte)0xFE);
        var ppt = new PptBuilder();
        ppt.Blips.Add(Atom(0xF01A, [.. new byte[16], .. header, .. packed.ToArray()], 0x3D4));
        var slide = new Sheet();
        slide.Shapes.Add(Shape(3001, 75, (0, 0, 1000, 1000), [(0x0104, 1)]));
        ppt.Slides.Add(slide);
        var ex = Assert.Throws<ConversionRefusedException>(() => PptReader.ToPptx(Save(ppt), DocumentLimits.Default, out _, Token).Dispose());
        Assert.Equal(DocumentLimits.ErrorKey, ex.ErrorKey);
    }
}
