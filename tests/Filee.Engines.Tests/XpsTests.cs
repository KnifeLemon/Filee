// Filee's own XPS / OpenXPS reader (Xps/) and its route through the drawing converter: packages written in code
// (XpsBuilder) in both namespaces, drawn at 96 dpi (one pixel per XPS unit) and checked by their pixels, glyph runs
// with an obfuscated embedded font checked by the PDF's text, and hostile packages refused quickly.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Rendering;
using Filee.Engines.Xps;
using ImageMagick;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public sealed class XpsTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private readonly string _folder = fx.NewFolder();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static byte[] TestFont() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf"));

    private string Save(XpsBuilder builder, string name = "test.xps") => builder.Save(Path.Combine(_folder, name));

    /// <summary>Page <paramref name="index"/> of the package as a picture at 96 dpi: one pixel per XPS unit.</summary>
    private SKBitmap Render(XpsBuilder builder, int index = 0)
    {
        using var document = XpsDocument.Open(Save(builder), Token);
        return document.Page(index, Token).Render(96);
    }

    private static void AssertColor(SKBitmap bitmap, int x, int y, string expected, int tolerance = 24)
    {
        var want = SKColor.Parse(expected);
        var got = bitmap.GetPixel(x, y);
        Assert.True(Math.Abs(got.Red - want.Red) <= tolerance && Math.Abs(got.Green - want.Green) <= tolerance && Math.Abs(got.Blue - want.Blue) <= tolerance,
            $"({x}, {y}) is {got}, expected {want}");
    }

    private static string Box(double x, double y, double width, double height) =>
        FormattableString.Invariant($"M {x},{y} L {x + width},{y} {x + width},{y + height} {x},{y + height} Z");

    private static byte[] Png(SKColor color, int width = 4, int height = 4, double dpi = 0)
    {
        using var image = new MagickImage(new MagickColor(color.Red, color.Green, color.Blue), (uint)width, (uint)height);
        if (dpi > 0)
            image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static byte[] Picture(SKColor color, MagickFormat format)
    {
        using var image = new MagickImage(new MagickColor(color.Red, color.Green, color.Blue), 4, 4);
        return image.ToByteArray(format);
    }

    // ───────────────────────── Package ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_namespaces_give_their_pages_sizes_and_title(bool openXps)
    {
        var builder = new XpsBuilder { OpenXps = openXps, Title = "Quarterly plan" }
            .Page($"<Path Data='{Box(0, 0, 10, 10)}' Fill='#FF000000'/>")
            .Page("", 400, 300);
        using var document = XpsDocument.Open(Save(builder), Token);
        Assert.Equal(2, document.Count);
        Assert.Equal("Quarterly plan", document.Title);
        Assert.Equal((612f, 792f), (document.Page(0, Token).Width, document.Page(0, Token).Height));
        Assert.Equal((300f, 225f), (document.Page(1, Token).Width, document.Page(1, Token).Height));
    }

    [Fact]
    public void Interleaved_pages_are_joined_and_a_page_missing_a_piece_is_damage()
    {
        using (var bitmap = Render(new XpsBuilder { Pieces = 3 }.Page($"<Path Data='{Box(10, 10, 50, 50)}' Fill='#00FF00'/>", 100, 100)))
            AssertColor(bitmap, 30, 30, "#00FF00");

        var path = Save(new XpsBuilder { Pieces = 3 }.Page("<Path Data='M 0,0 L 1,1'/>"), "pieces.xps");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            zip.GetEntry("Documents/1/Pages/1.fpage/[1].piece")!.Delete();
        using var document = XpsDocument.Open(path, Token);
        Assert.Throws<InvalidDataException>(() => document.Page(0, Token));
    }

    [Fact]
    public void Obfuscated_fonts_are_restored_with_the_guid_of_their_name()
    {
        var font = new byte[40];
        Assert.True(XpsPackage.Deobfuscate(font, "00112233-4455-6677-8899-AABBCCDDEEFF"));
        // Byte i is XORed with byte 15 - i % 16 of the GUID's text.
        Assert.Equal([0xFF, 0xEE, 0xDD], font[..3]);
        Assert.Equal(0x00, font[15]);
        Assert.Equal(0xFF, font[16]);
        Assert.Equal(0x00, font[32]);
        Assert.False(XpsPackage.Deobfuscate(new byte[40], "not-a-guid"));
    }

    [Theory]
    [InlineData("Documents/1/Pages/1.fpage", "../../../Resources/a%20b.png", "Resources/a b.png")]
    [InlineData("Documents/1/Pages/1.fpage", "/Resources/Fonts/x.odttf#1", "Resources/Fonts/x.odttf")]
    [InlineData("Documents/1/Pages/1.fpage", "../../../../escape.png", null)]
    [InlineData("Documents/1/Pages/1.fpage", "http://example.com/a.png", null)]
    public void References_resolve_inside_the_package(string from, string reference, string? expected) =>
        Assert.Equal(expected, XpsPackage.Resolve(from, reference));

    [Fact]
    public void A_zip_without_a_page_list_is_not_an_xps_document()
    {
        var path = Path.Combine(_folder, "plain.xps");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => XpsDocument.Open(path, Token));
        File.WriteAllText(path, "not a zip");
        Assert.Throws<InvalidDataException>(() => XpsDocument.Open(path, Token));
    }

    // ───────────────────────── Paths ─────────────────────────

    [Fact]
    public void Paths_are_filled_and_stroked_where_their_transforms_put_them()
    {
        var builder = new XpsBuilder().Page(
            $"<Canvas RenderTransform='1,0,0,1,100,100'><Path Data='{Box(0, 0, 50, 50)}' Fill='#FF0000' Stroke='#0000FF' StrokeThickness='4'/></Canvas>" +
            $"<Path Data='{Box(0, 0, 10, 10)}' Fill='#00FF00'><Path.RenderTransform><MatrixTransform Matrix='2,0,0,2,20,20'/></Path.RenderTransform></Path>",
            300, 300);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 125, 125, "#FF0000");
        AssertColor(bitmap, 100, 125, "#0000FF");
        AssertColor(bitmap, 30, 30, "#00FF00");
        AssertColor(bitmap, 42, 42, "#FFFFFF");
        AssertColor(bitmap, 200, 200, "#FFFFFF");
    }

    [Fact]
    public void The_abbreviated_syntax_reads_every_command()
    {
        var budget = new XpsBudget(XpsLimits.Default);
        using (var geometry = XpsGeometry.Parse("F1 M10,10 h20 v20 H10 Z m 40,0 c 0,10 10,10 10,0 s 10,-10 10,0 q 5,5 10,0 l 5,5 L 100 100", budget))
        {
            Assert.Equal(SKPathFillType.Winding, geometry.Fill.FillType);
            // Control points count: the smooth curve's reflected one lies at y = 0.
            Assert.Equal(new SKRect(10, 0, 100, 100), geometry.Fill.Bounds);
        }
        using (var geometry = XpsGeometry.Parse("M 0,0 L 10,0 garbage 20,20", budget))
            Assert.Equal(new SKRect(0, 0, 10, 0), geometry.Fill.Bounds); // ends where the data breaks
        Assert.Equal(SKPathFillType.EvenOdd, XpsGeometry.Parse("M 0,0 L 1,1", budget).Fill.FillType);

        // A circle of two arcs: filled inside, not in the corners of its box.
        using var bitmap = Render(new XpsBuilder().Page("<Path Data='M 100,50 A 50,50 0 1 1 100,150 A 50,50 0 1 1 100,50 Z' Fill='#000000'/>", 200, 200));
        AssertColor(bitmap, 100, 100, "#000000");
        AssertColor(bitmap, 56, 100, "#000000");
        AssertColor(bitmap, 55, 55, "#FFFFFF");
    }

    [Fact]
    public void Path_geometry_elements_fill_and_stroke_their_figures_and_segments()
    {
        // The first figure isn't filled, only stroked; the second one's middle segment isn't stroked.
        var geometry = "<PathGeometry FillRule='NonZero'>" +
                       "<PathFigure StartPoint='10,10' IsFilled='false' IsClosed='true'><PolyLineSegment Points='90,10 90,90 10,90'/></PathFigure>" +
                       "<PathFigure StartPoint='120,10' IsClosed='true'><PolyLineSegment Points='190,10'/><PolyLineSegment Points='190,90' IsStroked='false'/>" +
                       "<ArcSegment Point='120,90' Size='40,40' SweepDirection='Clockwise' RotationAngle='0' IsLargeArc='false'/></PathFigure></PathGeometry>";
        using var bitmap = Render(new XpsBuilder().Page($"<Path Fill='#FF0000' Stroke='#0000FF' StrokeThickness='4'><Path.Data>{geometry}</Path.Data></Path>", 200, 100));
        AssertColor(bitmap, 50, 50, "#FFFFFF");
        AssertColor(bitmap, 10, 50, "#0000FF");
        AssertColor(bitmap, 150, 30, "#FF0000");
        AssertColor(bitmap, 186, 50, "#FF0000"); // the unstroked edge
        AssertColor(bitmap, 150, 10, "#0000FF");
    }

    [Fact]
    public void Dashes_draw_gaps()
    {
        using var bitmap = Render(new XpsBuilder().Page(
            "<Path Data='M 0,10 L 200,10' Stroke='#000000' StrokeThickness='10' StrokeDashArray='2 2' StrokeDashCap='Flat'/>", 200, 20));
        AssertColor(bitmap, 10, 10, "#000000");
        AssertColor(bitmap, 30, 10, "#FFFFFF");
        AssertColor(bitmap, 50, 10, "#000000");
    }

    [Fact]
    public void Clips_opacity_and_opacity_masks_apply()
    {
        var builder = new XpsBuilder().Page(
            $"<Canvas Clip='{Box(0, 0, 50, 50)}'><Path Data='{Box(0, 0, 100, 100)}' Fill='#FF0000'/></Canvas>" +
            $"<Path Data='{Box(100, 0, 50, 50)}' Fill='#FF0000' Opacity='0.5'/>" +
            $"<Path Data='{Box(0, 100, 200, 50)}' Fill='#0000FF'><Path.OpacityMask><LinearGradientBrush MappingMode='Absolute' StartPoint='0,0' EndPoint='200,0'>" +
            "<LinearGradientBrush.GradientStops><GradientStop Color='#FF000000' Offset='0'/><GradientStop Color='#00000000' Offset='1'/></LinearGradientBrush.GradientStops>" +
            "</LinearGradientBrush></Path.OpacityMask></Path>",
            200, 200);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 25, 25, "#FF0000");
        AssertColor(bitmap, 75, 75, "#FFFFFF");
        AssertColor(bitmap, 125, 25, "#FF8080", 8);
        AssertColor(bitmap, 2, 125, "#0000FF");
        AssertColor(bitmap, 198, 125, "#FFFFFF");
    }

    // ───────────────────────── Brushes ─────────────────────────

    [Fact]
    public void Gradients_run_from_their_first_stop_to_their_last()
    {
        var stops = "<GradientStop Color='#FF0000' Offset='0'/><GradientStop Color='#0000FF' Offset='1'/>";
        var builder = new XpsBuilder().Page(
            $"<Path Data='{Box(0, 0, 200, 50)}'><Path.Fill><LinearGradientBrush MappingMode='Absolute' StartPoint='0,0' EndPoint='200,0'>" +
            $"<LinearGradientBrush.GradientStops>{stops}</LinearGradientBrush.GradientStops></LinearGradientBrush></Path.Fill></Path>" +
            $"<Path Data='{Box(0, 100, 100, 100)}'><Path.Fill><RadialGradientBrush MappingMode='Absolute' Center='50,150' GradientOrigin='50,150' RadiusX='50' RadiusY='50'>" +
            $"<RadialGradientBrush.GradientStops>{stops}</RadialGradientBrush.GradientStops></RadialGradientBrush></Path.Fill></Path>" +
            // Stops beyond 0…1 are cut there: the colour at 0 lies halfway between the stops at -1 and 1.
            $"<Path Data='{Box(100, 100, 100, 100)}'><Path.Fill><LinearGradientBrush MappingMode='Absolute' StartPoint='100,0' EndPoint='200,0'>" +
            "<LinearGradientBrush.GradientStops><GradientStop Color='#000000' Offset='-1'/><GradientStop Color='#FFFFFF' Offset='1'/></LinearGradientBrush.GradientStops>" +
            "</LinearGradientBrush></Path.Fill></Path>",
            200, 200);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 3, 25, "#FF0000");
        AssertColor(bitmap, 197, 25, "#0000FF");
        AssertColor(bitmap, 50, 150, "#FF0000");
        AssertColor(bitmap, 97, 150, "#0000FF", 40);
        AssertColor(bitmap, 101, 150, "#808080", 16);
    }

    [Fact]
    public void Image_brushes_draw_png_jpeg_and_tiff_at_their_resolution()
    {
        var builder = new XpsBuilder();
        // 4 × 4 pixels at 192 dpi are 2 × 2 units.
        var png = builder.Image(Png(SKColors.Lime, dpi: 192), "png");
        var jpeg = builder.Image(Picture(SKColors.Red, MagickFormat.Jpeg), "jpg");
        var tiff = builder.Image(Picture(SKColors.Blue, MagickFormat.Tiff), "tif");
        builder.Page(
            $"<Path Data='{Box(0, 0, 100, 100)}'><Path.Fill><ImageBrush ImageSource='{png}' Viewbox='0,0,2,2' ViewboxUnits='Absolute' Viewport='0,0,100,100' ViewportUnits='Absolute'/></Path.Fill></Path>" +
            $"<Path Data='{Box(100, 0, 100, 100)}'><Path.Fill><ImageBrush ImageSource='../../../Resources/Images/image2.jpg' Viewbox='0,0,4,4' Viewport='100,0,100,100'/></Path.Fill></Path>" +
            $"<Path Data='{Box(200, 0, 100, 100)}'><Path.Fill><ImageBrush ImageSource='{tiff}' Viewbox='0,0,4,4' Viewport='200,0,100,100'/></Path.Fill></Path>",
            300, 100);
        _ = jpeg;
        using var bitmap = Render(builder);
        AssertColor(bitmap, 90, 90, "#00FF00");
        AssertColor(bitmap, 150, 50, "#FF0000", 40);
        AssertColor(bitmap, 250, 50, "#0000FF");
    }

    [Fact]
    public void Tiled_image_and_visual_brushes_repeat_and_flip()
    {
        var builder = new XpsBuilder();
        using var halves = new MagickImage(MagickColors.White, 2, 1);
        halves.GetPixels().SetPixel(0, 0, [0, 0, 0]);
        var picture = builder.Image(halves.ToByteArray(MagickFormat.Png), "png");
        var visual = $"<Path Data='{Box(0, 0, 5, 10)}' Fill='#000000'/>";
        builder.Page(
            $"<Path Data='{Box(0, 0, 100, 20)}'><Path.Fill><ImageBrush ImageSource='{picture}' Viewbox='0,0,2,1' Viewport='0,0,20,20' TileMode='Tile'/></Path.Fill></Path>" +
            $"<Path Data='{Box(0, 20, 100, 20)}'><Path.Fill><ImageBrush ImageSource='{picture}' Viewbox='0,0,2,1' Viewport='0,20,20,20' TileMode='FlipX'/></Path.Fill></Path>" +
            $"<Path Data='{Box(0, 40, 100, 20)}'><Path.Fill><VisualBrush Viewbox='0,0,10,10' Viewport='0,40,20,20' TileMode='Tile'><VisualBrush.Visual>{visual}</VisualBrush.Visual></VisualBrush></Path.Fill></Path>",
            100, 60);
        using var bitmap = Render(builder);
        // Tile: black, white, black, white…
        AssertColor(bitmap, 5, 10, "#000000");
        AssertColor(bitmap, 15, 10, "#FFFFFF");
        AssertColor(bitmap, 25, 10, "#000000");
        // FlipX: black, white, white, black…
        AssertColor(bitmap, 25, 30, "#FFFFFF");
        AssertColor(bitmap, 35, 30, "#000000");
        // The visual: its left half black in every 20-unit tile.
        AssertColor(bitmap, 25, 50, "#000000");
        AssertColor(bitmap, 35, 50, "#FFFFFF");
    }

    [Fact]
    public void Static_resources_come_from_the_page_canvases_and_remote_dictionaries()
    {
        var builder = new XpsBuilder();
        var remote = builder.Dictionary($"<SolidColorBrush x:Key='Blue' Color='#0000FF'/><PathGeometry x:Key='Square' Figures='{Box(0, 0, 40, 40)}'/>");
        builder.Page(
            "<FixedPage.Resources><ResourceDictionary><SolidColorBrush x:Key='Red' Color='#FF0000'/></ResourceDictionary></FixedPage.Resources>" +
            $"<Path Data='{Box(0, 0, 40, 40)}' Fill='{{StaticResource Red}}'/>" +
            $"<Canvas><Canvas.Resources><ResourceDictionary Source='{remote}'/></Canvas.Resources>" +
            "<Path Data='{StaticResource Square}' Fill='{StaticResource Blue}' RenderTransform='1,0,0,1,50,0'/></Canvas>",
            100, 50);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 20, 20, "#FF0000");
        AssertColor(bitmap, 70, 20, "#0000FF");
    }

    // ───────────────────────── Glyphs ─────────────────────────

    [Fact]
    public async Task Glyphs_in_an_obfuscated_font_become_text_in_the_pdf()
    {
        var builder = new XpsBuilder();
        var font = builder.Font(TestFont());
        builder.Page($"<Glyphs FontUri='{font}' FontRenderingEmSize='40' OriginX='20' OriginY='60' UnicodeString='Filee' Fill='#000000'/>" +
                     $"<Glyphs FontUri='{font}' FontRenderingEmSize='20' OriginX='20' OriginY='100' UnicodeString='{{}}{{Hamburg}}' StyleSimulations='BoldSimulation' Fill='#FF0000'/>",
            400, 150);
        var source = Save(builder);
        using (var document = XpsDocument.Open(source, Token))
        {
            var page = document.Page(0, Token);
            // Plain runs go to the page model as text, so the PDF writer subsets their font.
            var runs = page.Items.OfType<GlyphItem>().Select(g => g.Run).ToList();
            Assert.Equal(2, runs.Count);
            Assert.Equal("{Hamburg}", runs[1].Text); // "{}" escapes a string that starts with a brace
            Assert.True(runs[1].Embolden);
            using var bitmap = page.Render(96);
            Assert.Contains(Enumerable.Range(20, 120), x => bitmap.GetPixel(x, 50).Red < 100);
        }
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", "pdf", new Preset(), new FolderOutput(_folder), _folder), null, Token);
        using var pdf = PdfDocument.Open(Assert.Single(written));
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Filee", text, StringComparison.Ordinal);
        Assert.Contains("Hamburg", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Indices_set_glyphs_advances_offsets_and_clusters()
    {
        using var font = PageFont.FromData(TestFont());
        var typeface = font.Typeface;
        var e = typeface.GetGlyph('e');
        // "ffi" as one glyph for three characters, then 'e' with an advance of half the em and one moved up by a tenth.
        var layout = XpsGlyphLayout.Lay(typeface, 100, new SKPoint(10, 200), "ffiee", $"(3:1){e};{e},50;{e},,0,10", rightToLeft: false);
        Assert.Equal([e, e, e], layout.Glyphs);
        Assert.Equal([0, 3, 4], layout.Clusters);
        Assert.Equal(200f - 10, layout.Positions[2].Y);
        Assert.Equal(50f, layout.Positions[2].X - layout.Positions[1].X, 3);

        // Right to left: each glyph left of the one before.
        var rtl = XpsGlyphLayout.Lay(typeface, 100, new SKPoint(500, 200), "Fil", null, rightToLeft: true);
        Assert.True(rtl.Positions[0].X < 500 && rtl.Positions[1].X < rtl.Positions[0].X && rtl.Positions[2].X < rtl.Positions[1].X);
    }

    // ───────────────────────── Converter ─────────────────────────

    [Fact]
    public void Xps_and_oxps_are_one_format_the_drawing_engine_reads()
    {
        Assert.Equal("xps", FormatRegistry.FindByExtension("oxps")?.Id);
        Assert.Equal("xps", FormatRegistry.FindByExtension("xps")?.Id);
        var engine = Assert.Single(fx.Converters.OfType<DrawingConverter>());
        Assert.Equal([("xps", "pdf"), ("xps", "svg"), ("xps", "png")], engine.Edges.Where(e => e.From == "xps").Select(e => (e.From, e.To)));
        Assert.All(engine.Edges.Where(e => e.From == "xps"), e => Assert.False(e.Checked));
        Assert.Equal("drawing", fx.Catalog.CreatePlanner([]).Plan("xps", "pdf")!.Steps[0].Converter.Id);
    }

    [Theory]
    [InlineData("pdf", 1)]
    [InlineData("svg", 2)]
    [InlineData("png", 2)]
    public async Task An_xps_is_converted_page_by_page(string target, int files)
    {
        var source = Save(new XpsBuilder()
            .Page($"<Path Data='{Box(10, 10, 50, 50)}' Fill='#FF0000' FixedPage.NavigateUri='https://filee.sh/'/>", 200, 100)
            .Page($"<Path Data='{Box(10, 10, 50, 50)}' Fill='#0000FF'/>", 200, 100));
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", target, new Preset(), new FolderOutput(_folder), _folder), null, Token);
        Assert.Equal(files, written.Count);
        Assert.All(written, path => Assert.True(new FileInfo(path).Length > 100));
        if (target == "pdf")
        {
            using var pdf = PdfDocument.Open(written[0]);
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Equal(150, pdf.GetPage(1).Width, 1);
            Assert.Single(pdf.GetPage(1).GetAnnotations());
            Assert.Contains("https://filee.sh/", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(written[0])), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Jpeg_xr_pictures_are_drawn_and_a_damaged_one_is_named()
    {
        var builder = new XpsBuilder();
        var grey = builder.Image(JpegXrTestFile.DcOnlyGrey(), "wdp");
        var damaged = builder.Image([0x49, 0x49, 0xBC, 0x01, .. new byte[60]], "wdp");
        var page = $"<Path Data='{Box(0, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{grey}' Viewbox='0,0,16,16' Viewport='0,0,50,50'/></Path.Fill></Path>" +
                   $"<Path Data='{Box(50, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{damaged}' Viewbox='0,0,4,4' Viewport='50,0,50,50'/></Path.Fill></Path>";
        if (OperatingSystem.IsWindows())
        {
            // A picture of Windows' own encoder, as its XPS writer stores them: red, half-transparent on the right.
            var path = Path.Combine(_folder, "red.jxr");
            var pixels = new byte[32 * 32 * 4];
            for (var i = 0; i < 32 * 32; i++)
                (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (0, 0, 255, (byte)(i % 32 < 16 ? 255 : 128));
            JpegXrBuilder.Write(path, 32, 32, JpegXrBuilder.Bgra32, pixels, 32 * 4, new Dictionary<string, object> { ["ImageQuality"] = 0.9f });
            var red = builder.Image(File.ReadAllBytes(path), "wdp");
            page += $"<Path Data='{Box(0, 50, 100, 50)}'><Path.Fill><ImageBrush ImageSource='{red}' Viewbox='0,0,32,32' Viewport='0,50,100,50'/></Path.Fill></Path>";
        }
        builder.Page(page, 100, 100);
        var source = Save(builder);
        using (var document = XpsDocument.Open(source, Token))
        using (var bitmap = document.Page(0, Token).Render(96))
        {
            AssertColor(bitmap, 25, 25, "#808080", 2);
            AssertColor(bitmap, 75, 25, "#FFFFFF");
            if (OperatingSystem.IsWindows())
            {
                AssertColor(bitmap, 20, 75, "#FF0000", 8);
                AssertColor(bitmap, 80, 75, "#FF8080", 12);
            }
        }
        LeftOutNotice? notice = null;
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", "pdf", new Preset(), new FolderOutput(_folder), _folder, LeftOut: n => notice = n), null, Token);
        Assert.Single(written);
        Assert.NotNull(notice);
        Assert.Equal(new LeftOutPart("notice.renderer_left_out.pictures", 1), Assert.Single(notice.Parts));
    }

    // ───────────────────────── Strokes, sideways glyphs, colours ─────────────────────────

    [Fact]
    public void Line_caps_are_flat_round_square_or_triangles_at_each_end()
    {
        // Horizontal lines 20 thick from x = 40 to 160: the caps reach 10 past each end.
        var builder = new XpsBuilder().Page(
            "<Path Data='M 40,20 L 160,20' Stroke='#000000' StrokeThickness='20' StrokeStartLineCap='Triangle' StrokeEndLineCap='Triangle'/>" +
            "<Path Data='M 40,60 L 160,60' Stroke='#000000' StrokeThickness='20' StrokeStartLineCap='Round' StrokeEndLineCap='Flat'/>" +
            "<Path Data='M 40,100 L 160,100' Stroke='#000000' StrokeThickness='20' StrokeStartLineCap='Flat' StrokeEndLineCap='Square'/>" +
            "<Path Data='M 40,140 L 160,140' Stroke='#80000000' StrokeThickness='20' StrokeStartLineCap='Triangle' StrokeEndLineCap='Round'/>",
            200, 160);
        using var bitmap = Render(builder);
        // Triangles: the tip on the line's axis, nothing at the corners of a square cap.
        AssertColor(bitmap, 33, 20, "#000000", 40);
        AssertColor(bitmap, 167, 20, "#000000", 40);
        AssertColor(bitmap, 33, 13, "#FFFFFF", 40);
        AssertColor(bitmap, 167, 27, "#FFFFFF", 40);
        // Round start, flat end.
        AssertColor(bitmap, 33, 60, "#000000", 40);
        AssertColor(bitmap, 164, 60, "#FFFFFF");
        // Flat start, square end: its corners are drawn.
        AssertColor(bitmap, 36, 100, "#FFFFFF");
        AssertColor(bitmap, 167, 93, "#000000", 40);
        // A translucent stroke stays even where the caps join it (no darker seam).
        Assert.InRange(Math.Abs(bitmap.GetPixel(42, 140).Red - bitmap.GetPixel(100, 140).Red), 0, 6);
        Assert.InRange(Math.Abs(bitmap.GetPixel(158, 140).Red - bitmap.GetPixel(100, 140).Red), 0, 6);
    }

    [Fact]
    public void Sideways_glyphs_are_turned_and_stay_text_in_the_pdf()
    {
        var builder = new XpsBuilder();
        var font = builder.Font(TestFont());
        // "l" upright is tall and thin; sideways it lies along the run, its top centre on the origin.
        builder.Page($"<Glyphs FontUri='{font}' FontRenderingEmSize='80' OriginX='20' OriginY='100' UnicodeString='l' Fill='#000000'/>" +
                     $"<Glyphs FontUri='{font}' FontRenderingEmSize='80' OriginX='150' OriginY='100' UnicodeString='l' IsSideways='true' Fill='#000000'/>",
            300, 200);
        using var document = XpsDocument.Open(Save(builder), Token);
        var page = document.Page(0, Token);
        var runs = page.Items.OfType<GlyphItem>().Select(g => g.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.NotNull(runs[1].Transform);
        using var bitmap = page.Render(96);
        var upright = Ink(bitmap, 0, 0, 140, 200);
        var sideways = Ink(bitmap, 140, 0, 300, 200);
        Assert.True(upright.Height > upright.Width * 2, $"upright {upright}");
        Assert.True(sideways.Width > sideways.Height * 2, $"sideways {sideways}");
        // Its ink starts right of the origin and straddles the baseline.
        Assert.True(sideways.Left >= 150 - 2 && sideways.Top < 100 && sideways.Bottom > 100, $"sideways {sideways}");
    }

    /// <summary>The box of the dark pixels inside a part of the picture.</summary>
    private static SKRectI Ink(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (bitmap.GetPixel(x, y).Red >= 128)
                    continue;
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
            }
        }
        return x0 == int.MaxValue ? SKRectI.Empty : new SKRectI(x0, y0, x1 + 1, y1 + 1);
    }

    [Fact]
    public void Context_colors_go_through_their_profile_or_follow_the_fallback_rules()
    {
        var builder = new XpsBuilder();
        var srgb = builder.Part("Resources/srgb.icc", Filee.Engines.Pdf.SrgbProfile.Bytes);
        var cmyk = builder.Part("Resources/swop.icc", ColorProfiles.USWebCoatedSWOP.ToByteArray());
        var junk = builder.Part("Resources/junk.icc", [1, 2, 3, 4, 5, 6, 7, 8]);
        builder.Page(
            // Through the sRGB profile: red.
            $"<Path Data='{Box(0, 0, 20, 20)}' Fill='ContextColor {srgb} 1,1,0,0'/>" +
            // Through a press CMYK profile: cyan as ink prints it, not the pure (0, 255, 255) of the formula.
            $"<Path Data='{Box(20, 0, 20, 20)}' Fill='ContextColor {cmyk} 1,1,0,0,0'/>" +
            // No usable profile: three channels are scRGB (linear 0.5 is sRGB 188), one or two the first as grey,
            // four CMYK by formula, more the first four as CMYK.
            $"<Path Data='{Box(40, 0, 20, 20)}' Fill='ContextColor {junk} 1,0.5,0.5,0.5'/>" +
            $"<Path Data='{Box(60, 0, 20, 20)}' Fill='ContextColor /missing.icc 1,0.25,0.9'/>" +
            $"<Path Data='{Box(80, 0, 20, 20)}' Fill='ContextColor /missing.icc 1,0,1,0,0,0.5,0.5'/>" +
            "<Path Data='M 0,20 L 100,20 100,40 0,40 Z'><Path.Fill><LinearGradientBrush MappingMode='Absolute' StartPoint='0,0' EndPoint='100,0'><LinearGradientBrush.GradientStops>" +
            $"<GradientStop Color='ContextColor {srgb} 1,0,0,1' Offset='0'/><GradientStop Color='ContextColor {srgb} 1,0,0,1' Offset='1'/>" +
            "</LinearGradientBrush.GradientStops></LinearGradientBrush></Path.Fill></Path>",
            100, 40);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 10, 10, "#FF0000", 6);
        var cyan = bitmap.GetPixel(30, 10);
        Assert.True(cyan.Red < 60 && cyan.Green is > 120 and < 220 && cyan.Blue > 200, $"cyan {cyan}");
        AssertColor(bitmap, 50, 10, "#BCBCBC", 3);
        AssertColor(bitmap, 70, 10, "#404040", 3);
        AssertColor(bitmap, 90, 10, "#FF00FF", 3);
        AssertColor(bitmap, 50, 30, "#0000FF", 6);
    }

    /// <summary>
    /// An ICC profile of <paramref name="tags"/> (signature, data), each tag aligned to four bytes; the header names the
    /// class, colour space and PCS and the version (2 or 4).
    /// </summary>
    private static byte[] Icc(string deviceClass, string space, string pcs, byte version, params (string Signature, byte[] Data)[] tags)
    {
        var offsets = new int[tags.Length];
        var at = 128 + 4 + 12 * tags.Length;
        for (var i = 0; i < tags.Length; i++)
        {
            offsets[i] = at;
            at = (at + tags[i].Data.Length + 3) & ~3;
        }
        var icc = new byte[at];
        void U32(int offset, long value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(offset), (uint)value);
        void Ascii(int offset, string text) => System.Text.Encoding.ASCII.GetBytes(text, icc.AsSpan(offset));
        U32(0, icc.Length);
        icc[8] = version;
        Ascii(12, deviceClass);
        Ascii(16, space);
        Ascii(20, pcs);
        Ascii(36, "acsp");
        U32(68, 63190); // D50 illuminant (s15Fixed16)
        U32(72, 65536);
        U32(76, 54061);
        U32(128, tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            Ascii(132 + 12 * i, tags[i].Signature);
            U32(132 + 12 * i + 4, offsets[i]);
            U32(132 + 12 * i + 8, tags[i].Data.Length);
            tags[i].Data.CopyTo(icc, offsets[i]);
        }
        return icc;
    }

    /// <summary>Lab as version 2's 16-bit PCS encoding (100 and +127 at 0xFF00).</summary>
    private static ushort[] LabV2(double l, double a, double b) =>
        [(ushort)Math.Round(l / 100 * 65280), (ushort)Math.Round((a + 128) * 256), (ushort)Math.Round((b + 128) * 256)];

    private static byte[] BigEndian(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * i), values[i]);
        return bytes;
    }

    /// <summary>
    /// A two-ink lut16 ('mft2', grid 2): no ink paper white, the first ink cyan, the second magenta, both a dark blue.
    /// </summary>
    internal static byte[] TwoInkProfile()
    {
        var lut = new List<byte>();
        lut.AddRange("mft2"u8.ToArray());
        lut.AddRange(new byte[4]);
        lut.AddRange([2, 3, 2, 0]);
        for (var i = 0; i < 9; i++)
            lut.AddRange(BigEndian(0, (ushort)(i % 4 == 0 ? 1 : 0), 0, 0).AsSpan(2, 4).ToArray()); // identity matrix, s15Fixed16
        lut.AddRange(BigEndian(2, 2));
        lut.AddRange(BigEndian(0, 65535, 0, 65535)); // input tables
        // CLUT, the first ink varying slowest: (0,0) (0,1) (1,0) (1,1).
        lut.AddRange(BigEndian([.. LabV2(100, 0, 0), .. LabV2(48, 74, -3), .. LabV2(55, -37, -50), .. LabV2(25, 20, -45)]));
        lut.AddRange(BigEndian(0, 65535, 0, 65535, 0, 65535)); // output tables
        return Icc("prtr", "2CLR", "Lab ", 2, ("A2B0", [.. lut]));
    }

    /// <summary>A Named Color profile ('nmcl', namedColor2Tag) of an orange and a blue ink.</summary>
    internal static byte[] NamedInkProfile()
    {
        var tag = new List<byte>();
        tag.AddRange("ncl2"u8.ToArray());
        tag.AddRange(new byte[8]);
        tag.AddRange(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 }); // two colours, no device values
        tag.AddRange(new byte[64]); // prefix and suffix
        foreach (var (name, lab) in new[] { ("Orange 021", LabV2(65, 50, 70)), ("Reflex Blue", LabV2(25, 20, -60)) })
        {
            var root = new byte[32];
            System.Text.Encoding.ASCII.GetBytes(name, root);
            tag.AddRange(root);
            tag.AddRange(BigEndian(lab));
        }
        return Icc("nmcl", "RGB ", "Lab ", 2, ("ncl2", [.. tag]));
    }

    [Fact]
    public void N_channel_and_named_color_profiles_give_their_inks()
    {
        var builder = new XpsBuilder();
        var inks = builder.Part("Resources/inks.icc", TwoInkProfile());
        var named = builder.Part("Resources/named.icc", NamedInkProfile());
        var broken = TwoInkProfile();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(132 + 4), 0x7FFF_0000); // the lut lies outside
        var damaged = builder.Part("Resources/damaged.icc", broken);
        builder.Page(
            $"<Path Data='{Box(0, 0, 20, 20)}' Fill='ContextColor {inks} 1,0,0'/>" +
            $"<Path Data='{Box(20, 0, 20, 20)}' Fill='ContextColor {inks} 1,1,0'/>" +
            $"<Path Data='{Box(40, 0, 20, 20)}' Fill='ContextColor {inks} 1,0,1'/>" +
            $"<Path Data='{Box(60, 0, 20, 20)}' Fill='ContextColor {inks} 1,1,1'/>" +
            $"<Path Data='{Box(0, 20, 20, 20)}' Fill='ContextColor {named} 1,1,0'/>" +
            $"<Path Data='{Box(20, 20, 20, 20)}' Fill='ContextColor {named} 1,0.4,0'/>" +
            $"<Path Data='{Box(40, 20, 20, 20)}' Fill='ContextColor {named} 1,0,0'/>" +
            $"<Path Data='{Box(60, 20, 20, 20)}' Fill='ContextColor {named} 1,1,1'/>" +
            // Not usable: two channels follow the rule for N ≤ 3, the first channel as grey.
            $"<Path Data='{Box(80, 0, 20, 40)}' Fill='ContextColor {damaged} 1,0.25,0.9'/>",
            100, 40);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 10, 10, "#FFFFFF", 3);
        var cyan = bitmap.GetPixel(30, 10);
        Assert.True(cyan is { Red: < 60, Green: > 110 and < 190, Blue: > 180 }, $"cyan {cyan}");
        var magenta = bitmap.GetPixel(50, 10);
        Assert.True(magenta is { Red: > 180, Green: < 60, Blue: > 80 and < 170 }, $"magenta {magenta}");
        var both = bitmap.GetPixel(70, 10);
        Assert.True(both is { Red: < 90, Green: < 70, Blue: > 100 } && both.Blue > both.Red, $"both {both}");

        var orange = bitmap.GetPixel(10, 30);
        Assert.True(orange is { Red: > 220, Green: > 80 and < 160, Blue: < 60 }, $"orange {orange}");
        var tint = bitmap.GetPixel(30, 30);
        Assert.True(tint.Red > 230 && tint.Green > orange.Green + 30 && tint.Blue > orange.Blue + 40, $"tint {tint}");
        AssertColor(bitmap, 50, 30, "#FFFFFF", 3);
        var duotone = bitmap.GetPixel(70, 30);
        Assert.True(duotone.Red < orange.Red - 100 && duotone.Green < orange.Green && duotone.Blue < 80, $"duotone {duotone}");
        AssertColor(bitmap, 90, 20, "#404040", 3);
    }

    [Fact]
    public void Sideways_glyphs_are_emboldened_and_italicised_about_their_sideways_origin()
    {
        using var font = PageFont.FromData(TestFont());
        var plain = XpsGlyphLayout.Lay(font.Typeface, 80, new SKPoint(20, 100), "ll", null, rightToLeft: false, sideways: true);
        var bold = XpsGlyphLayout.Lay(font.Typeface, 80, new SKPoint(20, 100), "ll", null, rightToLeft: false, sideways: true, bold: true);
        // An emboldened glyph's own vertical advance grows by 2 % of the em (1.6 at 80); the run goes down the turned frame.
        Assert.Equal(1.6f, (bold.Positions[1].Y - bold.Positions[0].Y) - (plain.Positions[1].Y - plain.Positions[0].Y), 2);
        var upright = XpsGlyphLayout.Lay(font.Typeface, 80, new SKPoint(20, 100), "ll", null, rightToLeft: false, bold: true);
        var regular = XpsGlyphLayout.Lay(font.Typeface, 80, new SKPoint(20, 100), "ll", null, rightToLeft: false);
        Assert.Equal(1.6f, upright.Positions[1].X - regular.Positions[1].X, 2);

        // Drawn: the italic "l" leans from its sideways origin (OriginX, its top): each column of ink lies lower by tan 20°
        // of its distance from there, the end near the origin by little, the far end by a quarter of the glyph more.
        var builder = new XpsBuilder();
        var part = builder.Font(TestFont());
        builder.Page($"<Glyphs FontUri='{part}' FontRenderingEmSize='80' OriginX='40' OriginY='60' UnicodeString='l' IsSideways='true' Fill='#000000'/>" +
                     $"<Glyphs FontUri='{part}' FontRenderingEmSize='80' OriginX='40' OriginY='160' UnicodeString='l' IsSideways='true' StyleSimulations='ItalicSimulation' Fill='#000000'/>",
            200, 220);
        using var bitmap = Render(builder);
        var straight = Ink(bitmap, 0, 0, 200, 110);
        var leaning = Ink(bitmap, 0, 110, 200, 220);
        // Rows of ink at the glyph's two ends: the far end lower than the near one by the skew.
        double Middle(int x, int top, int bottom)
        {
            var rows = Enumerable.Range(top, bottom - top).Where(y => bitmap.GetPixel(x, y).Red < 128).ToList();
            return rows.Count == 0 ? double.NaN : rows.Average();
        }
        var (near, far) = (leaning.Left + 3, leaning.Right - 3);
        var drop = Middle(far, 110, 220) - Middle(near, 110, 220);
        var expected = Math.Tan(20 * Math.PI / 180) * (far - near);
        Assert.InRange(drop, expected * 0.7, expected * 1.3);
        var lean = Math.Tan(20 * Math.PI / 180) * (near - 40);
        Assert.InRange(Middle(near, 110, 220) - 100 - Middle(near, 0, 110), lean - 3, lean + 3);
        Assert.InRange(Middle(straight.Right - 3, 0, 110) - Middle(straight.Left + 3, 0, 110), -1.5, 1.5);
    }

    [Fact]
    public void Upright_italic_simulation_skews_by_twenty_degrees()
    {
        // ECMA-388 §12.1.5: 20°, XPS's own value; other synthetic italics keep the renderer's.
        Assert.Equal(XpsGlyphs.ItalicSlant, -Math.Tan(20 * Math.PI / 180), 5);
        Assert.NotEqual(XpsGlyphs.ItalicSlant, GlyphRun.SyntheticSlant);

        // Drawn: the top of an italic "l" lies right of its foot by tan 20° of its height.
        var builder = new XpsBuilder();
        var part = builder.Font(TestFont());
        builder.Page($"<Glyphs FontUri='{part}' FontRenderingEmSize='120' OriginX='40' OriginY='140' UnicodeString='l' StyleSimulations='ItalicSimulation' Fill='#000000'/>",
            200, 160);
        using var bitmap = Render(builder);
        var ink = Ink(bitmap, 0, 0, 200, 160);
        double Middle(int y)
        {
            var columns = Enumerable.Range(0, 200).Where(x => bitmap.GetPixel(x, y).Red < 128).ToList();
            return columns.Count == 0 ? double.NaN : columns.Average();
        }
        var (top, foot) = (ink.Top + 4, ink.Bottom - 4);
        var lean = Middle(top) - Middle(foot);
        var expected = Math.Tan(20 * Math.PI / 180) * (foot - top);
        Assert.InRange(lean, expected * 0.8, expected * 1.2);
    }

    [Fact]
    public void Color_converted_pictures_are_read_through_their_profile()
    {
        var builder = new XpsBuilder();
        var picture = builder.Image(Png(new SKColor(40, 200, 60)), "png");
        var adobe = builder.Part("Resources/adobe.icc", ColorProfiles.AdobeRGB1998.ToByteArray());
        // The same picture with the Adobe RGB profile inside it.
        using var tagged = new MagickImage(new MagickColor(40, 200, 60), 4, 4);
        tagged.SetProfile(ColorProfiles.AdobeRGB1998);
        var embedded = builder.Image(tagged.ToByteArray(MagickFormat.Png), "png");
        builder.Page(
            $"<Path Data='{Box(0, 0, 20, 20)}'><Path.Fill><ImageBrush ImageSource='{picture}' Viewbox='0,0,4,4' Viewport='0,0,20,20'/></Path.Fill></Path>" +
            $"<Path Data='{Box(20, 0, 20, 20)}'><Path.Fill><ImageBrush ImageSource='{{ColorConvertedBitmap {picture} {adobe}}}' Viewbox='0,0,4,4' Viewport='20,0,20,20'/></Path.Fill></Path>" +
            $"<Path Data='{Box(40, 0, 20, 20)}'><Path.Fill><ImageBrush ImageSource='{embedded}' Viewbox='0,0,4,4' Viewport='40,0,20,20'/></Path.Fill></Path>",
            60, 20);
        using var bitmap = Render(builder);
        AssertColor(bitmap, 10, 10, "#28C83C", 3);
        // Adobe RGB's green is more saturated than sRGB's: read through it, the green comes out greener.
        foreach (var x in (int[])[30, 50])
        {
            var converted = bitmap.GetPixel(x, 10);
            Assert.True(converted.Red < 20 && converted.Blue < 50 && converted.Green > 190, $"converted {converted} at {x}");
        }
    }

    // ───────────────────────── Fonts ─────────────────────────

    /// <summary>
    /// The test font with the outline of "F" turned into a composite glyph whose components run past its end: Skia
    /// reads the font (it looks at outlines only to draw them), but the PDF writer's subsetter can't take that glyph.
    /// </summary>
    private static byte[] DamagedFont()
    {
        var font = TestFont();
        ushort glyph;
        using (var original = PageFont.FromData(font))
            glyph = original.Typeface.GetGlyph('F');
        int Table(string tag)
        {
            var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
            for (var i = 0; i < tables; i++)
            {
                var record = 12 + i * 16;
                if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag)
                    return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            }
            throw new InvalidDataException(tag);
        }
        var longLoca = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(Table("head") + 50)) != 0;
        var loca = Table("loca");
        int Offset(int g) => longLoca
            ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + g * 4))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(loca + g * 2)) * 2;
        var (start, end) = (Table("glyf") + Offset(glyph), Table("glyf") + Offset(glyph + 1));
        Assert.True(end - start >= 16);
        // numberOfContours −1, then components (MORE_COMPONENTS, glyph 0, byte arguments) to the end and beyond.
        font[start] = font[start + 1] = 0xFF;
        for (var at = start + 10; at + 6 <= end; at += 6)
            (font[at], font[at + 1], font[at + 2], font[at + 3], font[at + 4], font[at + 5]) = (0, 0x20, 0, 0, 0, 0);
        return font;
    }

    [Fact]
    public async Task A_damaged_embedded_font_is_replaced_and_the_pdf_still_written()
    {
        var damaged = DamagedFont();
        var sfnt = Filee.Engines.Fonts.SfntFont.ReadFace(damaged, 0);
        using (var check = PageFont.FromData(damaged))
        {
            var glyph = check.Typeface.GetGlyph('F');
            Assert.ThrowsAny<Exception>(() => Filee.Engines.Fonts.FontSubsetter.Subset(sfnt, [glyph], Token).Write());
        }
        var builder = new XpsBuilder();
        var broken = builder.Font(damaged);
        var good = builder.Font(TestFont());
        builder.Page($"<Glyphs FontUri='{broken}' FontRenderingEmSize='30' OriginX='10' OriginY='40' UnicodeString='Filee' Fill='#000000'/>" +
                     $"<Glyphs FontUri='{good}' FontRenderingEmSize='30' OriginX='10' OriginY='90' UnicodeString='Hamburg' Fill='#000000'/>" +
                     "<Glyphs FontUri='/Resources/Fonts/missing.odttf' FontRenderingEmSize='30' OriginX='10' OriginY='140' UnicodeString='Missing' Fill='#000000'/>",
            300, 160);
        var source = Save(builder);
        LeftOutNotice? notice = null;
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", "pdf", new Preset(), new FolderOutput(_folder), _folder, LeftOut: n => notice = n), null, Token);
        using var pdf = PdfDocument.Open(Assert.Single(written));
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Filee", text, StringComparison.Ordinal);
        Assert.Contains("Hamburg", text, StringComparison.Ordinal);
        Assert.NotNull(notice);
        Assert.Equal(new LeftOutPart("notice.renderer_left_out.fonts", 2), Assert.Single(notice.Parts));
    }

    // ───────────────────────── Tiled visual brushes ─────────────────────────

    [Fact]
    public async Task Tiled_visual_brushes_stay_vector_in_the_pdf_and_huge_ones_become_one_tile_picture()
    {
        var visual = $"<Path Data='{Box(0, 0, 5, 10)}' Fill='#000000'/>";
        var source = Save(new XpsBuilder().Page(
            $"<Path Data='{Box(0, 0, 200, 40)}'><Path.Fill><VisualBrush Viewbox='0,0,10,10' Viewport='0,0,20,20' TileMode='FlipX'><VisualBrush.Visual>{visual}</VisualBrush.Visual></VisualBrush></Path.Fill></Path>",
            200, 40), "vector.xps");
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", "pdf", new Preset(), new FolderOutput(_folder), _folder), null, Token);
        using (var pdf = PdfDocument.Open(Assert.Single(written)))
            Assert.Empty(pdf.GetPage(1).GetImages());
        using (var document = XpsDocument.Open(source, Token))
        using (var bitmap = document.Page(0, Token).Render(96))
        {
            // FlipX: black on the left of even tiles, on the right of odd ones.
            AssertColor(bitmap, 5, 10, "#000000");
            AssertColor(bitmap, 15, 10, "#FFFFFF");
            AssertColor(bitmap, 25, 10, "#FFFFFF");
            AssertColor(bitmap, 35, 10, "#000000");
        }

        // A tile of half a unit over a 2,000-unit square: 16 million tiles become one picture, repeated.
        var clock = Stopwatch.StartNew();
        var huge = Save(new XpsBuilder().Page(
            $"<Path Data='{Box(0, 0, 2000, 2000)}'><Path.Fill><VisualBrush Viewbox='0,0,10,10' Viewport='0,0,0.5,0.5' TileMode='Tile'><VisualBrush.Visual>{visual}</VisualBrush.Visual></VisualBrush></Path.Fill></Path>",
            2000, 2000), "huge.xps");
        var folder = Path.Combine(_folder, "huge");
        Directory.CreateDirectory(folder);
        var hugeWritten = await new DrawingConverter().ConvertAsync(
            new ConversionStep(huge, "xps", "pdf", new Preset(), new FolderOutput(folder), _folder), null, Token);
        Assert.True(new FileInfo(Assert.Single(hugeWritten)).Length < 4_000_000);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void Hostile_documents_are_refused_or_cut_quickly()
    {
        var clock = Stopwatch.StartNew();

        // Canvases nested deeper than the limit.
        var deep = string.Concat(Enumerable.Repeat("<Canvas>", 200)) + string.Concat(Enumerable.Repeat("</Canvas>", 200));
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page(deep), "deep.xps"), Token))
            Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => document.Page(0, Token)).ErrorKey);

        // A visual brush that draws itself ends at the nesting limit.
        var self = "<FixedPage.Resources><ResourceDictionary><VisualBrush x:Key='Me' Viewbox='0,0,10,10' Viewport='0,0,10,10' TileMode='Tile'>" +
                   $"<VisualBrush.Visual><Path Data='{Box(0, 0, 10, 10)}' Fill='{{StaticResource Me}}'/></VisualBrush.Visual></VisualBrush></ResourceDictionary></FixedPage.Resources>" +
                   $"<Path Data='{Box(0, 0, 100, 100)}' Fill='{{StaticResource Me}}'/>";
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page(self, 100, 100), "self.xps"), Token))
        using (var bitmap = document.Page(0, Token).Render(12))
            Assert.NotNull(bitmap);

        // More points, pages or path data than allowed.
        var points = "M 0,0" + string.Concat(Enumerable.Repeat(" L 1,1", 2_000));
        var limits = XpsLimits.Default with { MaxPoints = 1_000, MaxPages = 3, MaxPathData = 100_000 };
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page($"<Path Data='{points}' Fill='#000000'/>"), "points.xps"), Token, limits))
            Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => document.Page(0, Token)).ErrorKey);
        var many = new XpsBuilder();
        for (var i = 0; i < 4; i++)
            many.Page("");
        Assert.Equal(DocumentLimits.TooLargeKey, Assert.Throws<ConversionRefusedException>(() => XpsDocument.Open(Save(many, "pages.xps"), Token, limits)).ErrorKey);
        var data = "M 0,0" + string.Concat(Enumerable.Repeat(" ", 200_000));
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page($"<Path Data='{data}' Fill='#000000'/>"), "data.xps"), Token, limits))
            Assert.Equal(DocumentLimits.TooLargeKey, Assert.Throws<ConversionRefusedException>(() => document.Page(0, Token)).ErrorKey);

        // A picture whose header claims 100,000 × 100,000 pixels is left out, not decoded.
        var bomb = DocumentFuzzTests.TinyPng(4, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(20), 100_000);
        var builder = new XpsBuilder();
        var part = builder.Image(bomb, "png");
        builder.Page($"<Path Data='{Box(0, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{part}' Viewbox='0,0,4,4' Viewport='0,0,50,50'/></Path.Fill></Path>", 50, 50);
        using (var document = XpsDocument.Open(Save(builder, "bomb.xps"), Token))
        {
            using var bitmap = document.Page(0, Token).Render(24);
            Assert.Equal(1, Assert.Single(document.LeftOut).Count);
        }

        // More ICC profiles than are applied, and a profile larger than allowed: their colours follow the fallback rules.
        var profiles = new XpsBuilder();
        var colors = new System.Text.StringBuilder();
        var srgb = Filee.Engines.Pdf.SrgbProfile.Bytes;
        for (var i = 0; i < 80; i++)
            colors.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Path Data='{Box(i, 0, 1, 10)}' Fill='ContextColor {profiles.Part($"Resources/p{i}.icc", srgb)} 1,1,0,0'/>");
        var big = profiles.Part("Resources/big.icc", [.. srgb, .. new byte[5 * 1024 * 1024]]);
        colors.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Path Data='{Box(0, 10, 80, 10)}' Fill='ContextColor {big} 1,0,0,1'/>");
        using (var document = XpsDocument.Open(Save(profiles.Page(colors.ToString(), 80, 20), "profiles.xps"), Token))
        using (var bitmap = document.Page(0, Token).Render(96))
        {
            // Applied or not, (1, 0, 0) in an RGB profile or as scRGB is red; (0, 0, 1) blue.
            AssertColor(bitmap, 79, 5, "#FF0000", 6);
            AssertColor(bitmap, 40, 15, "#0000FF", 6);
        }

        // A JPEG XR picture whose codestream claims 65,535 × 65,535 pixels in a few bytes is left out at once.
        var claim = JpegXrTestFile.DcOnlyGrey();
        var at = Array.IndexOf(claim, (byte)'W');
        claim[at + 12] = claim[at + 13] = claim[at + 14] = claim[at + 15] = 0xFF;
        var claims = new XpsBuilder();
        var claimed = claims.Image(claim, "wdp");
        claims.Page($"<Path Data='{Box(0, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{claimed}' Viewbox='0,0,4,4' Viewport='0,0,50,50'/></Path.Fill></Path>", 50, 50);
        using (var document = XpsDocument.Open(Save(claims, "claim.xps"), Token))
        {
            using var bitmap = document.Page(0, Token).Render(24);
            Assert.Equal(1, Assert.Single(document.LeftOut).Count);
        }

        // A huge page is cut to PDF's 200 inches.
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page("", 1e9, 1e9), "huge.xps"), Token))
            Assert.Equal(14_400f, document.Page(0, Token).Width, 1);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}
