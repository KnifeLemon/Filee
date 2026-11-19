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
    public async Task A_jpeg_xr_picture_is_left_out_and_named_in_the_result()
    {
        var builder = new XpsBuilder();
        var hdPhoto = builder.Image([0x49, 0x49, 0xBC, 0x01, .. new byte[60]], "wdp");
        builder.Page($"<Path Data='{Box(0, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{hdPhoto}' Viewbox='0,0,4,4' Viewport='0,0,50,50'/></Path.Fill></Path>" +
                     $"<Path Data='{Box(50, 0, 50, 50)}'><Path.Fill><ImageBrush ImageSource='{hdPhoto}' Viewbox='0,0,4,4' Viewport='50,0,50,50'/></Path.Fill></Path>", 100, 50);
        var source = Save(builder);
        LeftOutNotice? notice = null;
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "xps", "pdf", new Preset(), new FolderOutput(_folder), _folder, LeftOut: n => notice = n), null, Token);
        Assert.Single(written);
        Assert.NotNull(notice);
        Assert.Equal("notice.renderer_left_out", notice.Key);
        Assert.Equal(new LeftOutPart("notice.renderer_left_out.pictures", 1), Assert.Single(notice.Parts));
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
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XpsDocument.Open(Save(many, "pages.xps"), Token, limits)).ErrorKey);
        var data = "M 0,0" + string.Concat(Enumerable.Repeat(" ", 200_000));
        using (var document = XpsDocument.Open(Save(new XpsBuilder().Page($"<Path Data='{data}' Fill='#000000'/>"), "data.xps"), Token, limits))
            Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => document.Page(0, Token)).ErrorKey);

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
