// ODG, OTG and FODG output without LibreOffice (DrawingConverter.Odg, OdpWriter.WriteDrawing, SvgDrawingReader): decks,
// SVG, metafiles, PDF pages, Visio drawings and whiteboards built in code become drawings that Filee's own ODG reader
// reads back with the same shapes, text and pictures; the routes take the built-in writer before LibreOffice where it
// draws the file, and hostile SVG and metafiles are refused through the limits. Nothing is read from disk but what the
// tests write.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OdgWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private const string Graphics = "application/vnd.oasis.opendocument.graphics";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ───────────────────────── Helpers ─────────────────────────

    private static XElement Part(string package, string name)
    {
        using var zip = ZipFile.OpenRead(package);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream).Root!;
    }

    private static string Mimetype(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        using var reader = new StreamReader(zip.GetEntry("mimetype")!.Open());
        return reader.ReadToEnd();
    }

    private static List<SlideElement> Leaves(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? Leaves(group.Children) : [e]).ToList();

    private static string Words(SlideDeck deck) =>
        string.Join("\n", deck.Slides.SelectMany(s => Leaves(s.Elements)).OfType<SlideShape>().Where(s => s.Text is not null)
            .SelectMany(s => s.Text!.Paragraphs).Select(p => string.Concat(p.Inlines.Select(i => i is HText t ? t.Text : ""))));

    private async Task<string> ConvertAsync(string source, string to, Preset? preset = null)
    {
        preset ??= new Preset();
        preset.TargetFormat = to;
        var job = await fx.ConvertAsync([source], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>The share of pixels whose colour differs by more than <paramref name="tolerance"/> in a channel.</summary>
    private static double Differs(SKBitmap a, SKBitmap b, int tolerance = 48)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var differ = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance)
                    differ++;
            }
        }
        return differ / (double)(a.Width * a.Height);
    }

    private const string Badge =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"400\" height=\"200\" viewBox=\"0 0 200 100\">" +
        "<rect x=\"10\" y=\"10\" width=\"180\" height=\"80\" rx=\"8\" fill=\"#33aa77\" stroke=\"#114422\" stroke-width=\"2\"/>" +
        "<circle cx=\"160\" cy=\"50\" r=\"20\" fill=\"#ffcc00\"/>" +
        "<text x=\"20\" y=\"58\" font-family=\"Arial\" font-size=\"20\" fill=\"#ffffff\">Filee</text>" +
        "{0}</svg>";

    // ───────────────────────── The writer ─────────────────────────

    [Fact]
    public void A_deck_written_as_a_drawing_reads_back_with_the_same_pages_and_no_presentation_parts()
    {
        var deck = PptxSlideReader.Read(OdpWriterTests.Sample().Save(Path.Combine(fx.NewFolder(), "deck.pptx")), cancellationToken: Token);
        var odg = Path.Combine(fx.NewFolder(), "drawing.odg");
        OdpWriter.WriteDrawing(deck, odg, cancellationToken: Token);

        Assert.Equal(Graphics, Mimetype(odg));
        using (var zip = ZipFile.OpenRead(odg))
            Assert.Equal("mimetype", zip.Entries[0].FullName);
        var manifest = Part(odg, "META-INF/manifest.xml");
        Assert.Equal(Graphics, (string?)manifest.Elements(Manifest + "file-entry").Single(e => (string?)e.Attribute(Manifest + "full-path") == "/").Attribute(Manifest + "media-type"));
        var content = Part(odg, "content.xml");
        var styles = Part(odg, "styles.xml");
        Assert.Equal("drawing", content.Element(Office + "body")!.Elements().Single().Name.LocalName);
        Assert.Equal(3, content.Descendants(Draw + "page").Count());
        // No notes, placeholders or presentation styles: Draw has none.
        foreach (var part in new[] { content, styles })
        {
            Assert.Empty(part.Descendants(Presentation + "notes"));
            Assert.DoesNotContain(part.Descendants().Attributes(), a => a.Name.Namespace == Presentation);
        }

        var back = OdfDrawReader.Read(odg, cancellationToken: Token);
        Assert.Equal(deck.Width, back.Width, 0.05f);
        Assert.Equal(deck.Height, back.Height, 0.05f);
        Assert.Equal(deck.Slides.Count, back.Slides.Count);
        for (var s = 0; s < deck.Slides.Count; s++)
        {
            Assert.Equal(Leaves(deck.Slides[s].Elements).Count, Leaves(back.Slides[s].Elements).Count);
            using var original = SlideOutput.ToBitmaps(deck, 48, [s], Token).Single();
            using var written = SlideOutput.ToBitmaps(back, 48, [s], Token).Single();
            var share = Differs(original, written);
            Assert.True(share < 0.01, $"page {s + 1}: {share:P2} of the pixels differ");
        }
        Assert.Contains("Built-in engines", Words(back), StringComparison.Ordinal);
    }

    // ───────────────────────── Sources ─────────────────────────

    [Fact]
    public async Task An_svg_becomes_a_drawing_with_editable_shapes_text_and_its_picture()
    {
        var folder = fx.NewFolder();
        var png = IwbBuilder.Png(20, 10, SKColors.Red, SKColors.Blue);
        var svg = Path.Combine(folder, "badge.svg");
        await File.WriteAllTextAsync(svg, string.Format(Badge,
            $"<image x=\"100\" y=\"20\" width=\"20\" height=\"10\" xlink:href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/>"), Token);

        var odg = await ConvertAsync(svg, "odg");
        Assert.Equal(Graphics, Mimetype(odg));
        var deck = OdfDrawReader.Read(odg, cancellationToken: Token);
        var page = Assert.Single(deck.Slides);
        // 400 × 200 px at 96 dpi.
        Assert.Equal(300, deck.Width, 0.5f);
        Assert.Equal(150, deck.Height, 0.5f);
        var shapes = Leaves(page.Elements).OfType<SlideShape>().ToList();
        Assert.Contains(shapes, s => s.Fill is SolidFill { Color: var c } && c == SKColor.Parse("#33aa77"));
        Assert.Contains(shapes, s => s.IsPicture);
        Assert.Equal("Filee", Words(deck));
        Assert.Contains(Part(odg, "content.xml").Descendants(Draw + "custom-shape"), e => e.Value.Contains("Filee", StringComparison.Ordinal));

        // Drawn as the vector engine draws the SVG: the green box and the yellow circle where they are.
        using var bitmap = SlideOutput.ToBitmaps(deck, 96, [0], Token).Single();
        Assert.Equal(SKColor.Parse("#33aa77"), bitmap.GetPixel(50, 170));
        var yellow = bitmap.GetPixel(320, 100);
        Assert.True(yellow.Red > 200 && yellow.Green > 170 && yellow.Blue < 60, yellow.ToString());

        // The template and the flat file of it.
        Assert.Equal("application/vnd.oasis.opendocument.graphics-template", Mimetype(await ConvertAsync(svg, "otg")));
        var fodg = await ConvertAsync(svg, "fodg");
        Assert.Equal(Graphics, (string?)XDocument.Load(fodg).Root!.Attribute(Office + "mimetype"));
        Assert.Equal("Filee", Words(OdfDrawReader.Read(fodg, cancellationToken: Token)));
    }

    [Theory]
    [InlineData("emf")]
    [InlineData("wmf")]
    public async Task A_metafile_becomes_a_page_of_its_size_with_the_metafile_as_its_picture(string format)
    {
        const uint Red = 0x0000FF;
        var bytes = format == "emf"
            ? new EmfBuilder(192, 96).Select(EmfBuilder.NullPen).Brush(1, Red).Select(1).Rectangle(8, 8, 88, 88).ToArray()
            : new WmfBuilder(192, 96).Brush(Red).Select(0).Rectangle(8, 8, 88, 88).ToArray();
        var path = Path.Combine(fx.NewFolder(), "picture." + format);
        await File.WriteAllBytesAsync(path, bytes, Token);

        var odg = await ConvertAsync(path, "odg");
        var (width, height) = Metafile.Size(bytes)!.Value;
        var deck = OdfDrawReader.Read(odg, cancellationToken: Token);
        Assert.Equal(width, deck.Width, 0.05f);
        Assert.Equal(height, deck.Height, 0.05f);
        var picture = Assert.Single(Leaves(Assert.Single(deck.Slides).Elements).OfType<SlideShape>());
        Assert.True(picture.IsPicture);
        using (var zip = ZipFile.OpenRead(odg))
            Assert.Equal(bytes, ReadAll(zip.Entries.Single(e => e.FullName.StartsWith("Pictures/", StringComparison.Ordinal) && e.FullName.EndsWith("." + format, StringComparison.Ordinal))));
        using var bitmap = SlideOutput.ToBitmaps(deck, 72, [0], Token).Single();
        var red = bitmap.GetPixel((int)(width * 0.25), (int)(height / 2));
        Assert.True(red.Red > 200 && red.Green < 60 && red.Blue < 60, red.ToString());
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Pdf_pages_become_drawing_pages_with_editable_text()
    {
        var builder = new PdfBuilder();
        var font = builder.NewPage(400, 300).Font();
        builder.Line("First page of the PDF", 18, font, 40, 60).Bar(40, 120, 200, 60, (200, 30, 30));
        builder.NewPage(400, 300).Line("Second page", 18, font, 40, 60);
        var pdf = builder.Save(Path.Combine(fx.NewFolder(), "pages.pdf"));

        var deck = OdfDrawReader.Read(await ConvertAsync(pdf, "odg"), cancellationToken: Token);
        Assert.Equal(2, deck.Slides.Count);
        Assert.Equal(400, deck.Width, 1f);
        Assert.Equal(300, deck.Height, 1f);
        var words = Words(deck);
        Assert.Contains("First page of the PDF", words, StringComparison.Ordinal);
        Assert.Contains("Second page", words, StringComparison.Ordinal);

        // Through the drawing to the template; the page range of the preset picks pages.
        var otg = await ConvertAsync(pdf, "otg", new Preset { Pdf = { PageRange = "2" } });
        Assert.Equal("application/vnd.oasis.opendocument.graphics-template", Mimetype(otg));
        var second = OdfDrawReader.Read(otg, cancellationToken: Token);
        Assert.Single(second.Slides);
        Assert.Contains("Second page", Words(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visio_whiteboards_and_decks_become_drawings()
    {
        var visio = new VsdxBuilder();
        visio.Page(VsdxBuilder.Shape(1, 2, 1.5, 2, 1, VsdxBuilder.Rectangle(2, 1) + VsdxBuilder.Filled("#0000FF") + VsdxBuilder.TextBlock("Visio box")), 4, 3);
        var vsdx = visio.Save(Path.Combine(fx.NewFolder(), "plan.vsdx"));
        var fromVisio = OdfDrawReader.Read(await ConvertAsync(vsdx, "odg"), cancellationToken: Token);
        Assert.Equal(4 * 72, fromVisio.Width, 0.5f);
        Assert.Equal("Visio box", Words(fromVisio));

        var iwb = IwbBuilder.Save(Path.Combine(fx.NewFolder(), "board.iwb"),
            IwbBuilder.Single("<svg:rect x=\"10\" y=\"10\" width=\"100\" height=\"50\" fill=\"#ff0000\"/><svg:text x=\"20\" y=\"100\" font-size=\"20\">Board</svg:text>"));
        var fromBoard = OdfDrawReader.Read(await ConvertAsync(iwb, "odg"), cancellationToken: Token);
        Assert.Equal("Board", Words(fromBoard));

        var pptx = OdpWriterTests.Sample().Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
        var fodg = await ConvertAsync(pptx, "fodg");
        Assert.Equal(Graphics, (string?)XDocument.Load(fodg).Root!.Attribute(Office + "mimetype"));
        var fromDeck = OdfDrawReader.Read(fodg, cancellationToken: Token);
        Assert.Equal(3, fromDeck.Slides.Count);
        Assert.Contains("Agenda", Words(fromDeck), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mind_map_becomes_a_drawing_of_its_topics()
    {
        var folder = fx.NewFolder();
        var mm = Path.Combine(folder, "map.mm");
        await File.WriteAllTextAsync(mm, "<map version=\"1.0.1\"><node TEXT=\"Centre\"><node TEXT=\"Left idea\" POSITION=\"left\"/>" +
                                         "<node TEXT=\"Right idea\" POSITION=\"right\"><node TEXT=\"Detail\"/></node></node></map>", Token);
        var deck = OdfDrawReader.Read(await ConvertAsync(mm, "odg"), cancellationToken: Token);
        var page = Assert.Single(deck.Slides);
        // Every topic is a shape of its own with its text.
        var topics = Leaves(page.Elements).OfType<SlideShape>().Where(s => s.Text is not null).ToList();
        foreach (var topic in new[] { "Centre", "Left idea", "Right idea", "Detail" })
            Assert.Contains(topics, s => Words(new SlideDeck { Width = 1, Height = 1, Slides = { new Slide { Elements = { s } } } }) == topic);
        var centre = topics.Single(s => s.Text!.Paragraphs.Any(p => p.Inlines.OfType<HText>().Any(t => t.Text == "Centre")));
        var left = topics.Single(s => s.Text!.Paragraphs.Any(p => p.Inlines.OfType<HText>().Any(t => t.Text == "Left idea")));
        Assert.True(left.Bounds.MidX < centre.Bounds.MidX);

        // A .mm of Objective-C++ source has no drawing.
        var source = Path.Combine(folder, "code.mm");
        await File.WriteAllTextAsync(source, "#import <Foundation/Foundation.h>\nint main() { return 0; }\n", Token);
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "odg" });
        Assert.Equal(JobState.Failed, job.State);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Routes_to_odg_take_the_built_in_writer_and_leave_libreoffice_what_it_does_better()
    {
        var planner = fx.Catalog.CreatePlanner(fx.Converters.Select(c => c.Id).ToArray());
        string[] Engines(string from, string to, bool withoutChecked = false) =>
            [.. planner.Plan(from, to, withoutChecked: withoutChecked)!.Steps.Select(s => s.Converter.Id)];

        foreach (var from in new[] { "vsd", "svg", "svgz", "emf", "wmf", "mm", "xmind", "pptx", "ppt", "odp", "fodp", "iwb", "notebook", "pdf" })
            Assert.Equal(["drawing"], Engines(from, "odg"));
        foreach (var to in new[] { "otg", "fodg" })
        {
            Assert.Equal(["drawing"], Engines("vsd", to));
            Assert.Equal(["drawing"], Engines("svg", to));
            Assert.Equal(["drawing", "odf-variants"], Engines("pdf", to));
        }
        // What Filee draws into a PDF of its own reaches ODG through it.
        Assert.Equal(["drawing", "drawing"], Engines("xps", "odg"));
        Assert.Equal(["drawing", "drawing"], Engines("crtx", "odg"));
        // The ODG forms become each other losslessly; formats only LibreOffice reads stay its own.
        Assert.Equal(["odf-variants"], Engines("fodg", "odg"));
        Assert.Equal(["libreoffice"], Engines("cdr", "odg"));
        // An SVG or a binary Visio file the built-in readers don't draw goes to LibreOffice Draw.
        Assert.Equal(["libreoffice"], Engines("svg", "odg", withoutChecked: true));
        Assert.Equal(["libreoffice"], Engines("vsd", "odg", withoutChecked: true));

        var drawing = fx.Converters.OfType<DrawingConverter>().Single();
        var odg = drawing.Edges.Where(e => e.To is "odg" or "otg" or "fodg").ToList();
        Assert.All(odg, e => Assert.True(e.Cost < 25, $"{e.From} → {e.To}"));
        Assert.All(odg.Where(e => e.From != "pdf"), e => Assert.True(e.DirectOnly, $"{e.From} → {e.To}"));
        Assert.DoesNotContain(odg, e => e.From is "odg" or "otg" or "fodg");
        Assert.False(drawing.Transforms("svg", "odg"));
    }

    [Fact]
    public void The_svg_check_names_what_the_reader_does_not_draw()
    {
        var folder = fx.NewFolder();
        string Check(string body)
        {
            var path = Path.Combine(folder, $"{Guid.NewGuid():N}.svg");
            File.WriteAllText(path, string.Format(Badge, body));
            return new DrawingConverter().FirstUnsupported(path, "svg") ?? "";
        }

        Assert.Equal("", Check(""));
        Assert.Equal("SVG filters", Check("<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"2\"/></filter></defs>"));
        Assert.Equal("CSS style sheets", Check("<style>rect { fill: red }</style>"));
        Assert.Equal("SVG clip paths", Check("<clipPath id=\"c\"><rect width=\"5\" height=\"5\"/></clipPath>"));
        Assert.Equal("linked pictures", Check("<image width=\"5\" height=\"5\" xlink:href=\"photo.png\"/>"));
        Assert.Equal("", Check("<image width=\"5\" height=\"5\" xlink:href=\"data:image/png;base64,AAAA\"/>"));
    }

    // ───────────────────────── Hostile files ─────────────────────────

    private static ConversionRefusedException RefusedQuickly(Action action)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(action);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        return refusal;
    }

    [Fact]
    public void Hostile_svg_and_metafiles_are_refused_through_the_limits()
    {
        var folder = fx.NewFolder();
        var limits = DocumentLimits.Default with { MaxPartBytes = 16 * 1024 };

        // An SVGZ that unpacks past a part's size.
        var svgz = Path.Combine(folder, "bomb.svgz");
        using (var file = File.Create(svgz))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\">" + new string(' ', 1024 * 1024) + "</svg>"));
        Assert.True(new FileInfo(svgz).Length < 16 * 1024);
        Assert.Contains("unpacks", RefusedQuickly(() => SvgDrawingReader.Read(svgz, limits, Token)).Detail);

        // A file larger than a part, and nesting past the XML depth.
        var large = Path.Combine(folder, "large.svg");
        File.WriteAllText(large, "<svg xmlns=\"http://www.w3.org/2000/svg\">" + new string(' ', 32 * 1024) + "</svg>");
        RefusedQuickly(() => SvgDrawingReader.Read(large, limits, Token));
        var deep = Path.Combine(folder, "deep.svg");
        File.WriteAllText(deep, "<svg xmlns=\"http://www.w3.org/2000/svg\">" + string.Concat(Enumerable.Repeat("<g>", 600)) + string.Concat(Enumerable.Repeat("</g>", 600)) + "</svg>");
        RefusedQuickly(() => SvgDrawingReader.Read(deep, cancellationToken: Token));
        // Not SVG: the reader's reason.
        var html = Path.Combine(folder, "page.svg");
        File.WriteAllText(html, "<html><body>no drawing</body></html>");
        Assert.Throws<InvalidDataException>(() => SvgDrawingReader.Read(html, cancellationToken: Token));

        // A metafile past its size limit, and one that isn't a metafile.
        var emf = Path.Combine(folder, "big.emf");
        File.WriteAllBytes(emf, new EmfBuilder(100, 100).Rectangle(0, 0, 50, 50).ToArray());
        var refused = Assert.Throws<ConversionRefusedException>(() => DrawingConverter.MetafilePage(emf, new MetafileLimits { MaxFileBytes = 64 }));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        var broken = Path.Combine(folder, "broken.wmf");
        File.WriteAllBytes(broken, Encoding.ASCII.GetBytes("not a metafile at all, just text"));
        Assert.Throws<InvalidDataException>(() => DrawingConverter.MetafilePage(broken));
    }
}
