// SVG output of the slide renderer (SlideOutput.ToSvg) and the drawing converter (Drawings/), tried with a test format
// whose reader is the PPTX slide reader (VisioTests try its VSDX format, OdfDrawTests its ODG / FODG ones). Decks are
// written in code.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Drawings;
using Filee.Engines.Slides;
using Filee.Engines.Vector;
using SkiaSharp;
using static Filee.Engines.Tests.SlideDeckBuilder;

namespace Filee.Engines.Tests;

public sealed class DrawingConverterTests : IDisposable
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-drawings-{Guid.NewGuid():N}");

    public DrawingConverterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Two slides: a filled shape with text, a picture and a line; then a text box.</summary>
    private string TwoPageDeck()
    {
        var deck = new SlideDeckBuilder();
        var picture = deck.Media(TestPicture(64, 40));
        deck.Slide(deck.Shape("roundRect", 40, 40, 400, 200, fill: Solid("#4472C4"), paragraphs: Paragraph("Filee 그림 ffi")) +
                   deck.Picture("rIdPic", 500, 40, 320, 200) +
                   deck.Connector("straightConnector1", 40, 300, 800, 0, "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln>"),
                pictures: new Dictionary<string, string> { ["rIdPic"] = picture })
            .Slide(deck.TextBox(60, 60, 600, 100, Paragraph("Second page")));
        return deck.Save(Path.Combine(_folder, "deck.pptx"));
    }

    // ───────────────────────── SVG ─────────────────────────

    [Fact]
    public void A_slide_as_svg_has_its_text_as_outlines_and_matches_its_picture()
    {
        var deck = PptxSlideReader.Read(TwoPageDeck(), cancellationToken: Token);
        var svgs = SlideOutput.ToSvg(deck, cancellationToken: Token).ToList();
        Assert.Equal(2, svgs.Count);

        var path = Path.Combine(_folder, "slide1.svg");
        File.WriteAllBytes(path, svgs[0]);
        var root = XDocument.Load(path).Root!;
        // 960 × 540 points at 96 dpi.
        Assert.Equal(("1280", "720"), ((string?)root.Attribute("width"), (string?)root.Attribute("height")));
        Assert.Empty(root.Descendants(Svg + "text"));
        Assert.NotEmpty(root.Descendants(Svg + "path"));
        Assert.Contains(root.Descendants(Svg + "image"), image =>
            ((string?)image.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href") ?? (string?)image.Attribute("href") ?? "").StartsWith("data:image/", StringComparison.Ordinal));

        // Drawn back, the SVG is the slide's picture at 96 dpi.
        using var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 96, [0], Token));
        var expected = Pixels(bitmap.Width, bitmap.Height, canvas => canvas.DrawBitmap(bitmap, 0, 0));
        using var drawing = SvgRenderer.Load(path);
        var drawn = Pixels(bitmap.Width, bitmap.Height, canvas => canvas.DrawPicture(drawing.Picture));
        Assert.True(Difference(expected, drawn) < 0.01, $"{Difference(expected, drawn):P2} of the pixels differ");
    }

    [Fact]
    public void Svg_output_follows_the_slides_asked_for()
    {
        var deck = PptxSlideReader.Read(TwoPageDeck(), cancellationToken: Token);
        var svg = Assert.Single(SlideOutput.ToSvg(deck, [1], Token));
        var path = Path.Combine(_folder, "slide2.svg");
        File.WriteAllBytes(path, svg);
        Assert.Empty(XDocument.Load(path).Root!.Descendants(Svg + "image"));
    }

    private static byte[] Pixels(int width, int height, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            draw(canvas);
        }
        return bitmap.GetPixelSpan().ToArray();
    }

    /// <summary>Share of pixels whose colour differs by more than <paramref name="tolerance"/> in some channel.</summary>
    private static double Difference(byte[] a, byte[] b, int tolerance = 64)
    {
        Assert.Equal(a.Length, b.Length);
        var different = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > tolerance || Math.Abs(a[i + 1] - b[i + 1]) > tolerance || Math.Abs(a[i + 2] - b[i + 2]) > tolerance)
                different++;
        }
        return different / (a.Length / 4.0);
    }

    // ───────────────────────── Converter ─────────────────────────

    [Fact]
    public void The_drawing_engine_is_registered_with_its_formats()
    {
        var engine = Assert.Single(EngineRegistry.CreateAll(new Infrastructure.EngineEnvironment(_folder)).OfType<DrawingConverter>());
        Assert.Equal("drawing", engine.Id);
        Assert.Equal(["vsd", "odg", "fodg"], engine.Edges.Select(e => e.From).Distinct());
        Assert.True(engine.GetStatus().IsAvailable);
        Assert.Null(engine.FirstUnsupported(Path.Combine(_folder, "a.odg"), "odg"));
    }

    /// <summary>A converter of one test format "testdraw" read as a deck, with <paramref name="check"/> as its check.</summary>
    private static DrawingConverter TestConverter(Func<string, string?>? check = null) =>
        new([new DrawingFormat("testdraw", (path, token) => PptxSlideReader.Read(path, cancellationToken: token), check)]);

    [Fact]
    public void Each_format_reaches_pdf_svg_and_png_and_its_check_is_asked()
    {
        Assert.Equal([("testdraw", "pdf"), ("testdraw", "svg"), ("testdraw", "png")], TestConverter().Edges.Select(e => (e.From, e.To)));
        var checkedConverter = TestConverter(path => path.EndsWith(".bad", StringComparison.Ordinal) ? "something" : null);
        Assert.Equal("something", checkedConverter.FirstUnsupported("x.bad", "testdraw"));
        Assert.Null(checkedConverter.FirstUnsupported("x.good", "testdraw"));
        Assert.Null(checkedConverter.FirstUnsupported("x.bad", "other"));

        var converter = TestConverter();
        Assert.True(converter.Transforms("testdraw", "png"));
        Assert.False(converter.Transforms("testdraw", "svg"));
        Assert.False(converter.Transforms("testdraw", "pdf"));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("svg")]
    [InlineData("png")]
    public async Task A_drawing_is_converted_a_file_per_page_or_one_pdf(string target)
    {
        var source = TwoPageDeck();
        var output = new FolderOutput(_folder);
        var written = await TestConverter().ConvertAsync(new ConversionStep(source, "testdraw", target, new Preset(), output, _folder), null, Token);
        if (target == "pdf")
        {
            var pdf = Assert.Single(written);
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
            Assert.Equal(2, document.NumberOfPages);
            return;
        }
        Assert.Equal(["_p1", "_p2"], output.Suffixes);
        Assert.Equal(2, written.Count);
        if (target == "svg")
        {
            Assert.All(written, file => Assert.Equal("svg", XDocument.Load(file).Root!.Name.LocalName));
            return;
        }
        using var picture = SKBitmap.Decode(written[0]);
        Assert.True(picture.Width > picture.Height);
    }

    [Fact]
    public async Task A_drawing_with_no_pages_is_refused()
    {
        var converter = new DrawingConverter([new DrawingFormat("empty", (_, _) => new SlideDeck { Width = 100, Height = 100 })]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            converter.ConvertAsync(new ConversionStep(Path.Combine(_folder, "a.empty"), "empty", "pdf", new Preset(), new FolderOutput(_folder), _folder), null, Token));
    }

    /// <summary>Hands out <c>out{suffix}.{ext}</c> paths in a folder and records the suffixes.</summary>
    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public List<string?> Suffixes { get; } = [];

        public string? Allocate(string extension, string? suffix = null)
        {
            Suffixes.Add(suffix);
            return Path.Combine(folder, $"out{suffix}.{extension}");
        }
    }
}
