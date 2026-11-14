// What PowerPoint 97–2003 shapes carry beyond a preset and a solid colour (Office/Ppt/PptToPptx.Geometry, .Fills):
// freeforms (vertices and segments, with arcs and quadrants), WordArt, gradient and pattern fills, shadows. Decks are
// built in code (PptBuilder), translated to PPTX, checked for validity and drawn by the slide renderer; what the renderer
// doesn't draw is named by the capability check so such decks keep LibreOffice.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office.Ppt;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.PptBuilder;

namespace Filee.Engines.Tests;

public sealed class PptShapeTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static uint Rgb(uint rrggbb) => PptReaderTests.Rgb(rrggbb);

    private static readonly (int, uint)[] Filled = [(0x01BF, 0x00100010), (0x01FF, 0x00080000)];

    private string Save(PptBuilder builder, string name = "deck.ppt")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static PptBuilder Deck(params byte[][] shapes)
    {
        var ppt = new PptBuilder();
        var slide = new Sheet();
        slide.Shapes.AddRange(shapes);
        ppt.Slides.Add(slide);
        return ppt;
    }

    /// <summary>The translated package's first slide, after checking the package is valid.</summary>
    private XElement Slide(string path)
    {
        var pptx = Path.Combine(fx.NewFolder(), "out.pptx");
        PptReader.ToPptx(path, pptx);
        PptxWriterTests.Valid(pptx);
        using var zip = ZipFile.OpenRead(pptx);
        using var stream = zip.GetEntry("ppt/slides/slide1.xml")!.Open();
        return XDocument.Load(stream).Root!;
    }

    private static SKBitmap Draw(string path) =>
        SlideOutput.ToBitmaps(PptxSlideReader.Read(path, cancellationToken: Token), 72, [0], Token).Single();

    private static string? Check(string path) => new FileeRendererConverter().FirstUnsupported(path, "ppt");

    private static bool Near(SKColor actual, SKColor expected, int tolerance = 12) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance;

    // ───────────────────────── Freeforms ─────────────────────────

    /// <summary>A triangle freeform: moveTo, two lineTo, close, end, in a 0..1000 geometry space.</summary>
    internal static byte[] Triangle(int spid, (int, int, int, int) anchor, uint color) =>
        Shape(spid, 0, anchor, [(0x0142, 1000), (0x0143, 1000), (0x0181, Rgb(color)), .. Filled], complex:
        [
            (0x0145, Vertices((500, 0), (1000, 1000), (0, 1000))),
            (0x0146, Segments(0x4000, 0x0002, 0x6001, 0x8000)),
        ]);

    [Fact]
    public void A_freeform_becomes_custom_geometry_the_renderer_fills()
    {
        var path = Save(Deck(Triangle(3001, (288, 288, 2016, 2016), 0xFF0000)));
        var slide = Slide(path);
        var geometry = slide.Descendants(A + "custGeom").Single();
        var drawn = geometry.Descendants(A + "path").Single();
        Assert.Equal("1000", (string?)drawn.Attribute("w"));
        Assert.Equal(["moveTo", "lnTo", "lnTo", "close"], drawn.Elements().Select(e => e.Name.LocalName));
        Assert.Null(Check(path));

        // 36..252 pt square: the apex at the top middle, the base along the bottom.
        using var picture = Draw(path);
        Assert.Equal(new SKColor(0xFF, 0, 0), picture.GetPixel(144, 220));
        Assert.Equal(SKColors.White, picture.GetPixel(50, 60)); // outside, top left
        Assert.Equal(SKColors.White, picture.GetPixel(240, 60)); // outside, top right
    }

    [Fact]
    public void Curves_quadrants_and_arcs_keep_their_shape()
    {
        // A circle of four elliptical quadrants around the box, and a half disc from an arcTo escape; open, so the
        // second path without noFill is filled as DrawingML fills open paths.
        var circle = Shape(3001, 0, (288, 288, 1440, 1440), [(0x0142, 1000), (0x0143, 1000), (0x0181, Rgb(0x0000FF)), .. Filled], complex:
        [
            (0x0145, Vertices((500, 0), (1000, 500), (500, 1000), (0, 500), (500, 0))),
            (0x0146, Segments(0x4000, 0xA704, 0x6001, 0x8000)),
        ]);
        var half = Shape(3002, 0, (2880, 288, 4032, 1440), [(0x0142, 1000), (0x0143, 1000), (0x0181, Rgb(0x00AA00)), .. Filled], complex:
        [
            // Box (0,0)-(1000,1000), from the ray to the right to the ray to the left, clockwise as seen: the bottom half.
            (0x0145, Vertices((1000, 500), (0, 0), (1000, 1000), (1000, 500), (0, 500))),
            (0x0146, Segments(0x4000, 0xA504, 0x6001, 0x8000)), // clockwiseArcTo (5)
        ]);
        var path = Save(Deck(circle, half));
        Assert.Null(Check(path));
        using var picture = Draw(path);
        // Circle: 36..180 pt, centre 108. Inside near the edge, outside at the box corner.
        Assert.Equal(new SKColor(0, 0, 0xFF), picture.GetPixel(108, 44));
        Assert.Equal(new SKColor(0, 0, 0xFF), picture.GetPixel(172, 108));
        Assert.Equal(SKColors.White, picture.GetPixel(40, 40));
        // Half disc: 360..504 pt, centre (432, 108): drawn below the centre, not above.
        Assert.Equal(new SKColor(0, 0xAA, 0), picture.GetPixel(432, 160));
        Assert.Equal(SKColors.White, picture.GetPixel(432, 60));
    }

    [Fact]
    public void Lines_or_curves_without_segment_information_follow_the_shape_path()
    {
        // shapePath 1: closed lines through the vertices.
        var square = Shape(3001, 0, (288, 288, 1440, 1440), [(0x0142, 100), (0x0143, 100), (0x0144, 1), (0x0181, Rgb(0xFF00FF)), .. Filled], complex:
        [
            (0x0145, Vertices((0, 0), (100, 0), (100, 100), (0, 100))),
        ]);
        var path = Save(Deck(square));
        var drawn = Slide(path).Descendants(A + "path").Single();
        Assert.Equal(["moveTo", "lnTo", "lnTo", "lnTo", "close"], drawn.Elements().Select(e => e.Name.LocalName));
        using var picture = Draw(path);
        Assert.Equal(new SKColor(0xFF, 0, 0xFF), picture.GetPixel(100, 100));
    }

    [Fact]
    public void Freeforms_Filee_cannot_draw_stay_named_for_the_check()
    {
        // Geometry through formulas (pGuides), and an angle ellipse escape.
        var guided = Shape(3001, 0, (288, 288, 1440, 1440), [(0x0181, Rgb(0xFF0000)), .. Filled], complex:
        [
            (0x0145, Vertices((0, 0), (100, 100))),
            (0x0156, MsoArray(8, Bytes(new byte[8]))),
        ]);
        Assert.Equal("freeform shapes", Check(Save(Deck(guided))));
        var ellipse = Shape(3001, 0, (288, 288, 1440, 1440), [(0x0181, Rgb(0xFF0000)), .. Filled], complex:
        [
            (0x0145, Vertices((0, 0), (100, 100), (0, 360))),
            (0x0146, Segments(0x4000, 0xA103, 0x8000)),
        ]);
        Assert.Equal("freeform shapes", Check(Save(Deck(ellipse))));
    }

    [Fact]
    public void A_deck_of_huge_freeforms_is_refused()
    {
        // 70 shapes of 16,000 points: past MaxPathPoints in the deck.
        var points = Enumerable.Range(0, 16_000).Select(i => (i % 1000, i / 16)).ToArray();
        var shapes = Enumerable.Range(0, 70).Select(i => Shape(3001 + i, 0, (0, 0, 100, 100), [.. Filled], complex:
        [
            (0x0145, Vertices(points)),
        ])).ToArray();
        var path = Save(Deck(shapes));
        var started = DateTime.UtcNow;
        var ex = Assert.Throws<ConversionRefusedException>(() => PptxSlideReader.Read(path, cancellationToken: Token));
        Assert.Equal("error.document_too_complex", ex.ErrorKey);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    // ───────────────────────── WordArt ─────────────────────────

    private static byte[] WordArt(int spid, int type, string text, uint color, bool outline = false) =>
        Shape(spid, type, (288, 1440, 5472, 2880),
            [(0x00FF, 0x40204020), (0x0181, Rgb(color)), (0x01BF, 0x00100010), (0x01FF, outline ? 0x00080008u : 0x00080000u)],
            complex: [(0x00C0, Unicode(text)), (0x00C5, Unicode("Arial Black"))]);

    [Fact]
    public void Plain_wordart_is_drawn_as_its_text_in_its_fill()
    {
        var path = Save(Deck(WordArt(3001, 136, "Hello", 0x0066CC)));
        var slide = Slide(path);
        Assert.Empty(slide.Descendants(A + "prstTxWarp"));
        Assert.Null(Check(path));

        var deck = PptxSlideReader.Read(path, cancellationToken: Token);
        var shape = deck.Slides[0].Elements.OfType<SlideShape>().Single();
        var run = shape.Text!.Paragraphs.Single().Inlines.OfType<HText>().Single();
        Assert.Equal("Hello", run.Text);
        Assert.Equal(true, run.Format.Bold);
        Assert.Equal("Arial Black", run.Format.Font);
        Assert.Equal("0066CC", run.Format.Color?.TrimStart('#'), ignoreCase: true);
        // Sized to fill the 648 × 180 pt box (by height here: 180 / 1.2 = 150 pt, wide enough for five letters).
        Assert.InRange(run.Format.Size ?? 0, 10_000, 15_000);
        Assert.Null(shape.Fill);
    }

    [Fact]
    public void Warped_or_outlined_wordart_is_named_for_the_check()
    {
        var arch = Save(Deck(WordArt(3001, 144, "Arch", 0x0066CC)));
        Assert.Equal("textArchUp", (string?)Slide(arch).Descendants(A + "prstTxWarp").Single().Attribute("prst"));
        Assert.Equal("WordArt text warp", Check(arch));
        // Without LibreOffice the renderer still draws the text, unwarped.
        Assert.Contains(PptxSlideReader.Read(arch, cancellationToken: Token).Slides[0].Elements.OfType<SlideShape>(), s => s.Text is { IsEmpty: false });

        Assert.Equal("outlined text", Check(Save(Deck(WordArt(3001, 136, "Lined", 0x0066CC, outline: true)))));
    }

    // ───────────────────────── Fills and shadows ─────────────────────────

    [Fact]
    public void Linear_gradients_run_along_the_fill_angle_with_the_last_colour_at_the_focus()
    {
        // fillAngle 0, focus 0: fillColor at the top, fillBackColor at the bottom.
        var vertical = Shape(3001, 1, (288, 288, 1440, 2880), [(0x0180, 7), (0x0181, Rgb(0xFF0000)), (0x0183, Rgb(0x0000FF)), .. Filled]);
        // fillAngle −90 (counter-clockwise), focus 100: fillColor on the right, fillBackColor on the left.
        var turned = Shape(3002, 1, (2880, 288, 5472, 1440), [(0x0180, 7), (0x018B, unchecked((uint)(-90 * 65536))), (0x018C, 100), (0x0181, Rgb(0xFF0000)), (0x0183, Rgb(0x0000FF)), .. Filled]);
        // Focus 50: the last colour in the middle, fillColor at both ends.
        var axial = Shape(3003, 1, (2880, 2016, 5472, 2880), [(0x0180, 7), (0x018C, 50), (0x0181, Rgb(0xFFFFFF)), (0x0183, Rgb(0x000000)), .. Filled]);
        var path = Save(Deck(vertical, turned, axial));
        Assert.Null(Check(path));
        using var picture = Draw(path);
        Assert.True(Near(picture.GetPixel(108, 40), new SKColor(0xFF, 0, 0), 30), picture.GetPixel(108, 40).ToString());
        Assert.True(Near(picture.GetPixel(108, 356), new SKColor(0, 0, 0xFF), 30), picture.GetPixel(108, 356).ToString());
        Assert.True(Near(picture.GetPixel(362, 108), new SKColor(0, 0, 0xFF), 30), picture.GetPixel(362, 108).ToString());
        Assert.True(Near(picture.GetPixel(682, 108), new SKColor(0xFF, 0, 0), 30), picture.GetPixel(682, 108).ToString());
        Assert.True(picture.GetPixel(522, 306).Red < 40); // the middle band of the axial gradient is dark
        Assert.True(picture.GetPixel(522, 254).Red > 200);
    }

    [Fact]
    public void Centre_gradients_and_multi_colour_gradients_keep_their_colours()
    {
        // shadeCenter from the middle (fillTo* 0.5), focus 100: fillBackColor in the middle, fillColor outside.
        var centre = Shape(3001, 1, (288, 288, 2016, 2016), [(0x0180, 5), (0x018C, 100), (0x018D, 0x8000), (0x018E, 0x8000), (0x018F, 0x8000), (0x0190, 0x8000),
            (0x0181, Rgb(0x00FF00)), (0x0183, Rgb(0xFFFF00)), .. Filled]);
        var stops = MsoArray(8, Bytes(Rgb(0xFF0000), 0), Bytes(Rgb(0x00FF00), 0x8000), Bytes(Rgb(0x0000FF), 0x10000));
        var rainbow = Shape(3002, 1, (2880, 288, 5472, 2016), [(0x0180, 7), (0x018B, unchecked((uint)(-90 * 65536))), (0x018C, 100), .. Filled], complex: [(0x0197, stops)]);
        var path = Save(Deck(centre, rainbow));
        var gradients = Slide(path).Descendants(A + "gradFill").ToList();
        Assert.Equal("rect", (string?)gradients[0].Element(A + "path")?.Attribute("path"));
        Assert.Equal(3, gradients[1].Descendants(A + "gs").Count());
        using var picture = Draw(path);
        Assert.True(Near(picture.GetPixel(144, 144), new SKColor(0xFF, 0xFF, 0), 40), picture.GetPixel(144, 144).ToString());
        Assert.True(Near(picture.GetPixel(40, 40), new SKColor(0, 0xFF, 0), 40), picture.GetPixel(40, 40).ToString());
        // From fillColor's end (the right, as the turned gradient above) to the last colour's.
        Assert.True(Near(picture.GetPixel(681, 144), new SKColor(0xFF, 0, 0), 40), picture.GetPixel(681, 144).ToString());
        Assert.True(Near(picture.GetPixel(522, 144), new SKColor(0, 0xFF, 0), 60), picture.GetPixel(522, 144).ToString());
        Assert.True(Near(picture.GetPixel(363, 144), new SKColor(0, 0, 0xFF), 40), picture.GetPixel(363, 144).ToString());
    }

    [Fact]
    public void A_pattern_fill_is_its_bitmap_in_the_two_colours()
    {
        // An 8 × 8 checker of 4 × 4 blocks: black top left and bottom right.
        using var bitmap = new SKBitmap(8, 8);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
                bitmap.SetPixel(x, y, (x < 4) == (y < 4) ? SKColors.Black : SKColors.White);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var ppt = Deck(Shape(3001, 1, (288, 288, 2016, 2016), [(0x0180, 1), (0x0186, 1), (0x0181, Rgb(0xFF0000)), (0x0183, Rgb(0xFFFF00)), .. Filled]));
        ppt.Pictures.Add(png.ToArray());
        var path = Save(ppt);
        Assert.Single(Slide(path).Descendants(A + "tile"));
        Assert.Null(Check(path));
        using var picture = SlideOutput.ToBitmaps(PptxSlideReader.Read(path, cancellationToken: Token), 288, [0], Token).Single();
        // A block is 4 pixels at 96 dpi, 3 pt, 12 device pixels at 288 dpi: both colours show whole.
        var colours = new HashSet<SKColor>();
        for (var x = 200; x < 260; x++)
            colours.Add(picture.GetPixel(x, 306)); // the middle of a row of blocks (the box starts at 144)
        Assert.Contains(colours, c => Near(c, new SKColor(0xFF, 0, 0)));
        Assert.Contains(colours, c => Near(c, new SKColor(0xFF, 0xFF, 0)));
    }

    [Fact]
    public void A_shadow_is_kept_in_the_package_and_named_for_the_check()
    {
        var path = Save(Deck(Shape(3001, 1, (288, 288, 2016, 2016), [(0x0181, Rgb(0xFF0000)), .. Filled, (0x023F, 0x00020002), (0x0205, 50800), (0x0206, 50800)])));
        var shadow = Slide(path).Descendants(A + "outerShdw").Single();
        Assert.Equal("71842", (string?)shadow.Attribute("dist"));
        Assert.Equal("2700000", (string?)shadow.Attribute("dir")); // 45°: down and to the right
        Assert.Equal("shadows and effects", Check(path));
        Assert.Null(Check(Save(Deck(Shape(3001, 1, (288, 288, 2016, 2016), [(0x0181, Rgb(0xFF0000)), .. Filled])))));
    }
}
