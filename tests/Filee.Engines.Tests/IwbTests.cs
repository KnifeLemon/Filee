// Interactive whiteboard files (.iwb, Whiteboard/): pages read into the slide model (checked as model values and as
// pixels at 96 dpi, one pixel per SVG unit), the drawing engine's PDF, ODP and PPTX, the left-out notice, the format's
// registration, and hostile packages. Files are written in code (IwbBuilder).

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;
using Filee.Engines.Whiteboard;
using SkiaSharp;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.IwbBuilder;

namespace Filee.Engines.Tests;

public sealed class IwbTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Write(byte[] package, string name = "lesson.iwb") => Save(Path.Combine(fx.NewFolder(), name), package);

    private static SlideDeck Read(string path, DocumentLimits? limits = null) => IwbReader.Read(path, limits, Token);

    private static IEnumerable<SlideElement> Flat(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? Flat(group.Children) : [e]);

    private static SKColor? FillColor(SlideElement element) => (element as SlideShape)?.Fill is SolidFill solid ? solid.Color : null;

    private static string Text(SlideShape shape) =>
        string.Join("\n", shape.Text!.Paragraphs.Select(p => string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))));

    private static void Near(SKRect expected, SKRect actual, float tolerance = 0.6f) =>
        Assert.True(Math.Abs(expected.Left - actual.Left) < tolerance && Math.Abs(expected.Top - actual.Top) < tolerance
                    && Math.Abs(expected.Width - actual.Width) < tolerance && Math.Abs(expected.Height - actual.Height) < tolerance,
            $"Expected {expected}, got {actual}.");

    // ───────────────────────── Model ─────────────────────────

    [Fact]
    public void Each_page_is_a_slide_of_the_drawings_size_with_its_shapes_text_and_pictures()
    {
        var deck = Read(Write(Lesson()));
        Assert.Equal((600f, 450f), (deck.Width, deck.Height));
        Assert.Equal("Fractions", deck.Title);
        Assert.Equal(2, deck.Slides.Count);

        var first = deck.Slides[0];
        Assert.Equal(new SolidFill(new SKColor(0xFF, 0xFF, 0xE0)), first.Background);
        var shapes = Flat(first.Elements).OfType<SlideShape>().ToList();
        // The banner outside the pageSet comes first, on every page.
        Near(new SKRect(0, 420, 600, 450), shapes[0].Bounds);
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), FillColor(shapes[0]));
        Near(new SKRect(0, 420, 600, 450), Flat(deck.Slides[1].Elements).First().Bounds);

        var box = shapes.Single(s => FillColor(s) == SKColors.Red);
        Near(SKRect.Create(75, 75, 150, 75), box.Bounds);
        Assert.Equal(3f, box.Line!.Width, 3);
        Assert.Equal(new SolidFill(SKColors.Black), box.Line.Fill);
        Assert.Contains(shapes, s => FillColor(s) == new SKColor(0, 128, 0));
        var ink = shapes.Single(s => s.Line?.Fill is SolidFill { Color: var c } && c == SKColors.Blue);
        Assert.Null(ink.Fill);
        Assert.Equal((4.5f, SKStrokeCap.Round), (ink.Line!.Width, ink.Line.Cap));
        Assert.DoesNotContain(shapes, s => FillColor(s) == SKColors.Magenta); // invisible on the board

        var text = shapes.Single(s => s.Text is not null && Text(s).Contains("quarter", StringComparison.Ordinal));
        Assert.Equal("Half and quarter", Text(text));
        var runs = text.Text!.Paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.Equal([null, true, null], runs.Select(r => r.Format.Bold));
        Assert.All(runs, r => Assert.Equal((3000, "Arial", "#000080"), (r.Format.Size!.Value, r.Format.Font, r.Format.Color)));
        Assert.False(text.Text.Wrap);
        Assert.Equal(75, text.Bounds.Left, 2);
        Assert.Equal(450 * 0.75f - 30 * SvgSlideReader.Ascent, text.Bounds.Top, 2);

        var area = shapes.Single(s => s.Text is not null && Text(s).StartsWith("First", StringComparison.Ordinal));
        Assert.Equal("First line\nSecond line", Text(area));
        Assert.True(area.Text!.Wrap);
        Assert.All(area.Text.Paragraphs, p => Assert.Equal(HAlign.Center, p.Format.Align));
        Near(SKRect.Create(337.5f, 225, 225, 150), area.Bounds);

        var picture = shapes.Single(s => s.IsPicture);
        Near(SKRect.Create(450, 300, 60, 30), picture.Bounds);
        Assert.IsType<PictureFill>(picture.Fill);

        Assert.Equal([("notice.renderer_left_out.videos", 1), ("notice.renderer_left_out.objects", 1)],
            deck.LeftOut.Select(p => (p.Key, p.Count)).OrderByDescending(p => p.Key));
    }

    [Fact]
    public void Transforms_turn_outlines_gradients_and_text_and_svg_pictures_and_uses_are_drawn()
    {
        var deck = Read(Write(Lesson()));
        var second = deck.Slides[1];
        Assert.Equal(new SolidFill(new SKColor(0xE0, 0xE0, 0xFF)), second.Background);
        var shapes = Flat(second.Elements).OfType<SlideShape>().ToList();

        // A 100 × 50 rectangle turned 90° about (400, 300): 50 wide and 100 high, its gradient running down.
        var turned = shapes.Single(s => s.Fill is GradientFill);
        Near(SKRect.Create(281.25f, 187.5f, 37.5f, 75), turned.Bounds);
        var gradient = (GradientFill)turned.Fill!;
        Assert.Equal(90, gradient.Angle, 2);
        Assert.Equal([SKColors.Blue, SKColors.Lime], gradient.Stops.Select(s => s.Color));

        var label = shapes.Single(s => s.Text is not null && Text(s) == "Turned");
        Assert.Equal(90, label.Rotation, 2);
        Assert.Equal(HAlign.Center, label.Text!.Paragraphs[0].Format.Align);

        // The star: an SVG picture with a DOCTYPE, scaled from 100 to 200 px at (50, 50), with its own picture.
        var star = shapes.Single(s => FillColor(s) == new SKColor(0xFF, 0xA5, 0x00));
        Near(SKRect.Create(37.5f, 37.5f, 150, 150), star.Bounds);
        var dot = shapes.Single(s => s.IsPicture);
        Near(SKRect.Create(97.5f, 127.5f, 30, 30), dot.Bounds);

        // use: the first page's box, 200 px lower.
        var copy = shapes.Single(s => FillColor(s) == SKColors.Red);
        Near(SKRect.Create(75, 225, 150, 75), copy.Bounds);
    }

    [Fact]
    public void A_page_is_drawn_as_the_svg_shows_it()
    {
        var deck = Read(Write(Lesson()));
        using var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 96, [0], Token));
        Assert.Equal((800, 600), (bitmap.Width, bitmap.Height));
        Assert.Equal(new SKColor(0xFF, 0xFF, 0xE0), bitmap.GetPixel(20, 20)); // the background, not the hidden square
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), bitmap.GetPixel(400, 580));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(200, 150));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(200, 100));
        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(500, 150));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(200, 350));
        Assert.Equal(SKColors.Yellow, bitmap.GetPixel(615, 420));
        Assert.Equal(SKColors.Cyan, bitmap.GetPixel(665, 420));
        // The text's ink sits on its baseline at y = 450.
        var ink = Enumerable.Range(100, 300).SelectMany(x => Enumerable.Range(400, 70).Select(y => (x, y)))
            .Where(p => bitmap.GetPixel(p.x, p.y) is var c && c.Blue > 100 && c.Red < 60 && c.Green < 60).ToList();
        Assert.NotEmpty(ink);
        Assert.InRange(ink.Max(p => p.y), 445, 462);
        Assert.InRange(ink.Min(p => p.y), 412, 425);
    }

    // ───────────────────────── Converter ─────────────────────────

    private static ConversionStep Step(string source, string target, string folder, Action<LeftOutNotice>? leftOut = null) =>
        new(source, "iwb", target, new Preset(), new FolderOutput(folder), folder, LeftOut: leftOut);

    [Fact]
    public async Task A_whiteboard_becomes_a_pdf_with_its_text_and_names_what_it_left_out()
    {
        var folder = fx.NewFolder();
        var source = Save(Path.Combine(folder, "lesson.iwb"), Lesson());
        LeftOutNotice? notice = null;
        var written = await new DrawingConverter().ConvertAsync(Step(source, "pdf", folder, n => notice = n), null, Token);
        using (var pdf = PdfDocument.Open(Assert.Single(written)))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Equal((600.0, 450.0), (pdf.GetPage(1).Width, pdf.GetPage(1).Height));
            var words = pdf.GetPage(1).GetWords().Select(w => w.Text).ToList();
            Assert.Contains("quarter", words);
            Assert.Contains("Second", words);
            // Turned text comes out of PdfPig letter by letter.
            Assert.Contains("Turned", string.Concat(pdf.GetPage(2).GetWords().Select(w => w.Text)), StringComparison.Ordinal);
        }
        Assert.Equal("notice.renderer_left_out", notice!.Key);
        Assert.Equal(2, notice.Parts.Count);
    }

    [Fact]
    public async Task A_whiteboard_becomes_odp_and_pptx_slides()
    {
        var folder = fx.NewFolder();
        var source = Save(Path.Combine(folder, "lesson.iwb"), Lesson());
        var odp = Assert.Single(await new DrawingConverter().ConvertAsync(Step(source, "odp", folder), null, Token));
        var back = OdfDrawReader.Read(odp, cancellationToken: Token);
        Assert.Equal(2, back.Slides.Count);
        Assert.Equal((600f, 450f), (back.Width, back.Height));
        Assert.Contains(Flat(back.Slides[0].Elements).OfType<SlideShape>(), s => s.Text is not null && Text(s).Contains("Second line", StringComparison.Ordinal));
        Assert.Contains(Flat(back.Slides[0].Elements).OfType<SlideShape>(), s => s.IsPicture);

        var pptx = Assert.Single(await new DrawingConverter().ConvertAsync(Step(source, "pptx", folder), null, Token));
        using var zip = ZipFile.OpenRead(pptx);
        var slides = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, slides.Count);
        using var reader = new StreamReader(zip.GetEntry("ppt/slides/slide1.xml")!.Open());
        Assert.Contains("quarter", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder, "slides-*.pdf")); // the PDF on the way is removed
    }

    [Fact]
    public void The_format_is_registered_and_routed_through_the_drawing_engine()
    {
        var format = FormatRegistry.FindById("iwb")!;
        Assert.Equal((FormatCategory.Presentation, false), (format.Category, format.Writable));
        Assert.Same(format, FormatRegistry.Detect("board.IWB"));
        var engine = fx.Converters.OfType<DrawingConverter>().Single();
        Assert.Equal(["pdf", "svg", "png", "odp", "pptx", "odg", "otg", "fodg", "emf", "wmf"], engine.Edges.Where(e => e.From == "iwb").Select(e => e.To));
        Assert.Equal(["pdf", "svg", "png", "odp", "pptx", "odg", "otg", "fodg", "emf", "wmf"], engine.Edges.Where(e => e.From == "notebook").Select(e => e.To));
        Assert.Equal(FormatCategory.Presentation, FormatRegistry.Detect("class.notebook")!.Category);
        Assert.All(engine.Edges.Where(e => e.From == "iwb" && e.To is "odp" or "pptx"), e => Assert.True(e.DirectOnly));
        Assert.False(engine.Transforms("iwb", "odp"));

        var planner = new RoutePlanner(fx.Converters);
        foreach (var target in new[] { "pdf", "odp", "pptx", "png", "jpg" })
            Assert.Equal("drawing", planner.Plan("iwb", target)!.Steps[0].Converter.Id);
    }

    [Fact]
    public async Task A_job_shows_the_left_out_notice()
    {
        var source = Write(Lesson());
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });
        var file = Assert.Single(job.Files);
        Assert.True(job.State == JobState.Completed, $"{file.ErrorKey} {file.ErrorDetail}");
        Assert.Equal("notice.renderer_left_out", file.LeftOut!.Key);
    }

    // ───────────────────────── SMART Notebook ─────────────────────────

    [Fact]
    public async Task Smart_notebook_pages_are_slides_in_the_manifests_order()
    {
        var pages = new[]
        {
            "<rect width=\"100\" height=\"100\" fill=\"#0000ff\"/><text x=\"10\" y=\"200\" font-size=\"30\">Page zero</text>",
            "<image x=\"100\" y=\"100\" width=\"50\" height=\"50\" xlink:href=\"images/pic.png\"/><notebook:extra/><video/>",
            "<g><text x=\"10\" y=\"50\"><tspan x=\"10\" y=\"50\">Line one</tspan><tspan x=\"10\" y=\"90\">Line two</tspan></text></g>",
        };
        var folder = fx.NewFolder();
        var deck = SmartNotebookReader.Read(Save(Path.Combine(folder, "a.notebook"), Notebook(pages, [2, 0, 1])), cancellationToken: Token);
        Assert.Equal((750f, 562.5f), (deck.Width, deck.Height));
        Assert.Equal(3, deck.Slides.Count);
        var lines = Flat(deck.Slides[0].Elements).OfType<SlideShape>().Select(Text).ToList();
        Assert.Equal(["Line one", "Line two"], lines);
        Assert.Contains(Flat(deck.Slides[1].Elements).OfType<SlideShape>(), s => s.Text is not null && Text(s) == "Page zero");
        Assert.True(((SlideShape)Assert.Single(deck.Slides[2].Elements)).IsPicture);
        Assert.Equal(("notice.renderer_left_out.videos", 1), (deck.LeftOut.Single().Key, deck.LeftOut.Single().Count));

        // Without a manifest the pages go by number (page10 after page2).
        var many = Enumerable.Range(0, 11).Select(i => $"<text y=\"20\">p{i}</text>").ToArray();
        deck = SmartNotebookReader.Read(Save(Path.Combine(folder, "b.notebook"), Notebook(many, null)), cancellationToken: Token);
        Assert.Equal(Enumerable.Range(0, 11).Select(i => $"p{i}"), deck.Slides.Select(slide => Text((SlideShape)slide.Elements.Single())));

        Assert.Throws<InvalidDataException>(() => SmartNotebookReader.Read(Save(Path.Combine(folder, "c.notebook"), Package("<x/>")), cancellationToken: Token));
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(Path.Combine(folder, "a.notebook"), "notebook", "pdf", new Preset(), new FolderOutput(folder), folder), null, Token);
        using var pdf = PdfDocument.Open(Assert.Single(written));
        Assert.Equal(3, pdf.NumberOfPages);
    }

    // ───────────────────────── Odd and hostile files ─────────────────────────

    [Fact]
    public void Files_that_are_no_whiteboards_are_refused_as_invalid()
    {
        Assert.Throws<InvalidDataException>(() => Read(Write("not a zip"u8.ToArray())));
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                zip.CreateEntry("other.xml");
            Assert.Contains("content.xml", Assert.Throws<InvalidDataException>(() => Read(Write(stream.ToArray()))).Message, StringComparison.Ordinal);
        }
        Assert.Throws<InvalidDataException>(() => Read(Write(Package("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"))));
        Assert.Throws<InvalidDataException>(() => Read(Write(Package(Content("")))));
        Assert.Throws<InvalidDataException>(() => Read(Write(Package("<iwb><svg"))));
        // Entities in a DTD (a billion laughs) are never expanded.
        var laughs = "<?xml version=\"1.0\"?><!DOCTYPE iwb [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
                     $"<iwb {Namespaces}><svg:svg><svg:text>&b;</svg:text></svg:svg></iwb>";
        Assert.Throws<InvalidDataException>(() => Read(Write(Package(laughs))));
    }

    [Fact]
    public void A_drawing_without_pages_or_size_is_one_board_sized_slide()
    {
        var deck = Read(Write(Package(Content("<svg:svg><svg:rect width=\"10\" height=\"10\"/></svg:svg>"))));
        Assert.Equal((768f, 576f), (deck.Width, deck.Height));
        Assert.Single(Assert.Single(deck.Slides).Elements);
        // Lower-case viewbox and a page with a viewBox of its own.
        deck = Read(Write(Package(Content("<svg:svg viewbox=\"0 0 200 100\"><svg:pageSet><svg:page viewBox=\"0 0 400 200\">" +
                                          "<svg:rect x=\"200\" y=\"100\" width=\"200\" height=\"100\" fill=\"red\"/></svg:page></svg:pageSet></svg:svg>"))));
        Assert.Equal((150f, 75f), (deck.Width, deck.Height));
        Near(SKRect.Create(75, 37.5f, 75, 37.5f), Assert.Single(deck.Slides[0].Elements).Bounds);
    }

    [Fact]
    public void Hrefs_never_leave_the_package()
    {
        var folder = fx.NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "outside.png"), Png(4, 4, SKColors.Red, SKColors.Red));
        var body = "<svg:image width=\"10\" height=\"10\" xlink:href=\"../outside.png\"/>" +
                   "<svg:image width=\"10\" height=\"10\" xlink:href=\"images/../../outside.png\"/>" +
                   $"<svg:image width=\"10\" height=\"10\" xlink:href=\"file:///{Path.Combine(folder, "outside.png").Replace('\\', '/')}\"/>" +
                   $"<svg:image width=\"10\" height=\"10\" xlink:href=\"{Path.Combine(folder, "outside.png")}\"/>" +
                   "<svg:image width=\"10\" height=\"10\" xlink:href=\"https://example.invalid/a.png\"/>" +
                   "<svg:image width=\"10\" height=\"10\" xlink:href=\"%2E%2E/outside.png\"/>";
        var deck = Read(Save(Path.Combine(folder, "a.iwb"), Single(body)));
        Assert.Empty(deck.Slides[0].Elements);
        Assert.Equal(("notice.renderer_left_out.pictures", 6), (deck.LeftOut.Single().Key, deck.LeftOut.Single().Count));

        Assert.Equal("images/a.png", IwbReader.PackageFiles.Resolve("", "./images/a%20b/../a.png"));
        Assert.Equal("a.png", IwbReader.PackageFiles.Resolve("images", "/a.png"));
        Assert.Equal("images/x/a.png", IwbReader.PackageFiles.Resolve("images", "x/a.png#frag"));
        Assert.Null(IwbReader.PackageFiles.Resolve("images", "../../a.png"));
        Assert.Null(IwbReader.PackageFiles.Resolve("", "C:\\Windows\\win.ini"));
        // A data: URI picture is read.
        var dot = Convert.ToBase64String(Png(2, 2, SKColors.Blue, SKColors.Blue));
        deck = Read(Write(Single($"<svg:image width=\"10\" height=\"10\" xlink:href=\"data:image/png;base64,{dot}\"/>")));
        Assert.True(((SlideShape)Assert.Single(deck.Slides[0].Elements)).IsPicture);
    }

    [Fact]
    public void Use_loops_end_and_exponential_uses_deep_nesting_and_big_parts_are_refused_quickly()
    {
        var clock = Stopwatch.StartNew();
        var loop = Read(Write(Single("<svg:g id=\"a\"><svg:rect width=\"5\" height=\"5\"/><svg:use xlink:href=\"#b\"/></svg:g>" +
                                     "<svg:g id=\"b\"><svg:use xlink:href=\"#a\"/></svg:g><svg:use xlink:href=\"#self\" id=\"self\"/>")));
        Assert.Single(loop.Slides);

        // 2^40 copies of one rectangle through uses of uses.
        var uses = new StringBuilder("<svg:defs><svg:rect id=\"u0\" width=\"1\" height=\"1\"/>");
        for (var i = 1; i <= 40; i++)
            uses.Append($"<svg:g id=\"u{i}\"><svg:use xlink:href=\"#u{i - 1}\"/><svg:use xlink:href=\"#u{i - 1}\"/></svg:g>");
        uses.Append("</svg:defs><svg:use xlink:href=\"#u40\"/>");
        Refused(Single(uses.ToString()));

        Refused(Single(string.Concat(Enumerable.Repeat("<svg:g>", 400)) + "<svg:rect width=\"1\" height=\"1\"/>" + string.Concat(Enumerable.Repeat("</svg:g>", 400))));
        // Groups 60 deep are drawn; the model nests 16 of them, so the ODP writer (32 at most) takes the slide.
        var deep = Read(Write(Single(string.Concat(Enumerable.Repeat("<svg:g><svg:rect width=\"1\" height=\"1\"/>", 60)) + string.Concat(Enumerable.Repeat("</svg:g>", 60)))));
        Assert.Equal(60, Flat(deep.Slides[0].Elements).Count());
        OdpWriter.Write(deep, Path.Combine(fx.NewFolder(), "deep.odp"), cancellationToken: Token);
        Refused(Single("<svg:text>" + string.Concat(Enumerable.Repeat("<svg:tspan>", 400)) + "x" + string.Concat(Enumerable.Repeat("</svg:tspan>", 400)) + "</svg:text>"));

        // content.xml larger than a part may be, many entries, many pages.
        var small = DocumentLimits.Default with { MaxPartBytes = 64 * 1024, MaxPackageEntries = 10, MaxPages = 3 };
        RefusedTooLarge(Single("<svg:desc>" + new string(' ', 200_000) + "</svg:desc>"), small);
        RefusedTooLarge(Single("", Enumerable.Range(0, 20).ToDictionary(i => $"images/{i}.png", _ => new byte[] { 1 })), small);
        RefusedTooLarge(Package(Content("<svg:svg><svg:pageSet>" + string.Concat(Enumerable.Repeat("<svg:page/>", 4)) + "</svg:pageSet></svg:svg>")), small);
        Assert.Equal(3, Read(Write(Package(Content("<svg:svg><svg:pageSet>" + string.Concat(Enumerable.Repeat("<svg:page/>", 3)) + "</svg:pageSet></svg:svg>"))), small).Slides.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");
    }

    private void Refused(byte[] package, DocumentLimits? limits = null, string key = DocumentLimits.ErrorKey) =>
        Assert.Equal(key, Assert.Throws<ConversionRefusedException>(() => Read(Write(package), limits)).ErrorKey);

    private void RefusedTooLarge(byte[] package, DocumentLimits? limits = null) => Refused(package, limits, DocumentLimits.TooLargeKey);

    [Fact]
    public void Wild_numbers_and_broken_values_draw_what_they_can()
    {
        var deck = Read(Write(Single(
            "<svg:rect width=\"1e30\" height=\"1e30\" fill=\"red\"/>" +
            "<svg:rect width=\"10\" height=\"10\" transform=\"scale(1e30) matrix(1 2 3)\"/>" +
            "<svg:rect width=\"NaN\" height=\"-5\"/><svg:circle r=\"-1\"/><svg:path d=\"M 0 0 L\"/><svg:path d=\"nonsense\"/>" +
            "<svg:polyline points=\"1\"/><svg:line x2=\"50\" stroke=\"#zzz\"/>" +
            "<svg:rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"url(#missing) #00ff00\" stroke=\"url(#none)\" stroke-dasharray=\"4 2\"/>" +
            "<svg:text font-size=\"-3\">a</svg:text><svg:textarea width=\"auto\" height=\"auto\">b<svg:tbreak/></svg:textarea>" +
            "<svg:image width=\"10\" height=\"10\" xlink:href=\"data:image/png;base64,@@@\"/><svg:foreignObject width=\"10\" height=\"10\"/>")));
        var shapes = deck.Slides[0].Elements.OfType<SlideShape>().ToList();
        Assert.Contains(shapes, s => FillColor(s) == SKColors.Lime && s.Line is null);
        Assert.Contains(shapes, s => s.Text is not null && Text(s) == "a");
        Assert.Contains(shapes, s => s.Text is not null && Text(s) == "b");
        Assert.All(deck.Slides[0].Elements, e => Assert.True(e.Bounds.Width <= 1e6 && e.Bounds.Height <= 1e6));
        using var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 24, [0], Token));
    }

    [Fact]
    public void Xhtml_text_in_a_foreign_object_is_kept()
    {
        var deck = Read(Write(Single(
            "<svg:foreignObject x=\"10\" y=\"10\" width=\"200\" height=\"100\"><body xmlns=\"http://www.w3.org/1999/xhtml\">" +
            "<p style=\"text-align:center;font-size:20px;color:#ff0000\">Hello <b>board</b></p><div>Second<br/>third</div></body></svg:foreignObject>")));
        var shape = (SlideShape)Assert.Single(deck.Slides[0].Elements);
        Assert.Equal("Hello board\nSecond\nthird", Text(shape));
        var runs = shape.Text!.Paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.Equal(("#FF0000", 1500, true), (runs[1].Format.Color, runs[1].Format.Size!.Value, runs[1].Format.Bold!.Value));
        Assert.Equal(HAlign.Center, shape.Text.Paragraphs[0].Format.Align);
    }

    [Fact]
    public void Reading_stops_when_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var path = Write(Lesson());
        Assert.ThrowsAny<OperationCanceledException>(() => IwbReader.Read(path, cancellationToken: cancelled.Token));
    }

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}
