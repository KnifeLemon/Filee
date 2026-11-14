// What Visio 2013+ drawings take from their theme and the newer ShapeSheet cells (Visio/VisioTheme, VisioReader):
// "Themed" colours, fill and line styles and fonts through the quick style cells and variants, FillGradient sections,
// shadows, text backgrounds, rounded corners, layers that don't print, and the check naming what isn't drawn. Drawings
// are built in code (VsdxBuilder); pages are 4 × 3 inches and drawn at 72 dpi (a pixel a point), y up in Visio.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Slides;
using Filee.Engines.Visio;
using SkiaSharp;
using static Filee.Engines.Tests.VsdxBuilder;

namespace Filee.Engines.Tests;

public sealed class VisioThemeTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string DrawingMl = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>
    /// A theme: accent1 blue, accent2 orange, dk1 near-black; one variant colour scheme (varColor1 purple, varColor2
    /// teal); Verdana as the body font; fill styles solid, a two-stop gradient, none; line styles 1 pt, 3 pt, none; an
    /// effect style without effects and one with a soft shadow.
    /// </summary>
    internal static string Theme =>
        $"<?xml version='1.0' encoding='UTF-8'?><a:theme xmlns:a='{DrawingMl}' xmlns:vt='http://schemas.microsoft.com/office/visio/2012/theme' name='Test'>" +
        "<a:themeElements><a:clrScheme name='Test'>" +
        "<a:dk1><a:srgbClr val='101010'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:dk2><a:srgbClr val='202020'/></a:dk2><a:lt2><a:srgbClr val='EEEEEE'/></a:lt2>" +
        "<a:accent1><a:srgbClr val='0000FF'/></a:accent1><a:accent2><a:srgbClr val='FF8000'/></a:accent2><a:accent3><a:srgbClr val='808080'/></a:accent3>" +
        "<a:accent4><a:srgbClr val='FFC000'/></a:accent4><a:accent5><a:srgbClr val='5B9BD5'/></a:accent5><a:accent6><a:srgbClr val='70AD47'/></a:accent6>" +
        "<a:hlink><a:srgbClr val='0563C1'/></a:hlink><a:folHlink><a:srgbClr val='954F72'/></a:folHlink>" +
        "<a:extLst><a:ext uri='{C8C0A9A3-1E1A-4D2D-9E4A-3B2B0B0B0B0B}'><vt:variationClrSchemeLst><vt:variationClrScheme>" +
        "<vt:varColor1><a:srgbClr val='800080'/></vt:varColor1><vt:varColor2><a:srgbClr val='008080'/></vt:varColor2>" +
        "</vt:variationClrScheme></vt:variationClrSchemeLst></a:ext></a:extLst></a:clrScheme>" +
        "<a:fontScheme name='Test'><a:majorFont><a:latin typeface='Georgia'/><a:ea typeface=''/><a:cs typeface=''/></a:majorFont>" +
        "<a:minorFont><a:latin typeface='Verdana'/><a:ea typeface=''/><a:cs typeface=''/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name='Test'><a:fillStyleLst><a:solidFill><a:schemeClr val='phClr'/></a:solidFill>" +
        "<a:gradFill><a:gsLst><a:gs pos='0'><a:schemeClr val='phClr'/></a:gs><a:gs pos='100000'><a:srgbClr val='FFFFFF'/></a:gs></a:gsLst><a:lin ang='0' scaled='0'/></a:gradFill>" +
        "<a:noFill/></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w='12700'><a:solidFill><a:schemeClr val='phClr'/></a:solidFill></a:ln>" +
        "<a:ln w='38100'><a:solidFill><a:schemeClr val='phClr'/></a:solidFill></a:ln><a:ln w='12700'><a:noFill/></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle>" +
        "<a:effectStyle><a:effectLst><a:outerShdw blurRad='40000' dist='20000' dir='5400000'><a:srgbClr val='000000'/></a:outerShdw></a:effectLst></a:effectStyle>" +
        "</a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val='phClr'/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>" +
        "</a:themeElements></a:theme>";

    /// <summary>A style sheet (ID 1, on "No Style") whose fill, line and text cells come from the theme.</summary>
    private static string ThemedStyle(string quick) =>
        "<StyleSheet ID='1' NameU='Theme' LineStyle='0' FillStyle='0' TextStyle='0'>" +
        Cell("FillForegnd", "Themed") + Cell("FillPattern", "Themed") + Cell("LineColor", "Themed") + Cell("LineWeight", "Themed") +
        Cell("LinePattern", "Themed") + quick +
        "<Section N='Character'><Row IX='0'>" + Cell("Font", "Themed") + Cell("Color", "Themed") + "</Row></Section></StyleSheet>";

    private static VsdxBuilder Themed(string quick, string shapes, string pageSheet = "")
    {
        var builder = new VsdxBuilder { Theme = Theme };
        builder.StyleSheets += ThemedStyle(quick);
        builder.Page(shapes, 4, 3, pageSheet);
        return builder;
    }

    private const string OnTheme = "Type='Shape' LineStyle='1' FillStyle='1' TextStyle='1'";

    private string Save(VsdxBuilder builder) => builder.Save(Path.Combine(fx.NewFolder(), "drawing.vsdx"));

    private static SlideDeck Read(string path) => VisioReader.Read(path, Token);

    private static SKBitmap Draw(SlideDeck deck) => Assert.Single(SlideOutput.ToBitmaps(deck, 72, [0], Token));

    private static void AssertColor(SKColor expected, SKBitmap bitmap, int x, int y, int tolerance = 40)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(Math.Abs(actual.Red - expected.Red) < tolerance && Math.Abs(actual.Green - expected.Green) < tolerance
            && Math.Abs(actual.Blue - expected.Blue) < tolerance, $"pixel ({x}, {y}) is {actual}, expected {expected}");
    }

    private static IEnumerable<SlideElement> All(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? [e, .. All(group.Children)] : new[] { e });

    // ───────────────────────── Themes ─────────────────────────

    [Fact]
    public void Themed_cells_take_the_quick_style_colours_line_widths_and_font()
    {
        // Fill accent1 (2), line accent2 (3) at the second line style (3 pt), text dk1 (0) in the body font.
        var quick = Cell("QuickStyleFillColor", 2) + Cell("QuickStyleFillMatrix", 1) + Cell("QuickStyleLineColor", 3) +
                    Cell("QuickStyleLineMatrix", 2) + Cell("QuickStyleFontColor", 0);
        var path = Save(Themed(quick, Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + TextBlock("Themed"), OnTheme)));
        Assert.Null(VisioReader.FirstUnsupported(path, Token));
        var deck = Read(path);
        var shape = deck.Slides[0].Elements.OfType<SlideShape>().Single();
        Assert.Equal(new SKColor(0, 0, 0xFF), Assert.IsType<SolidFill>(shape.Fill).Color);
        Assert.Equal(new SKColor(0xFF, 0x80, 0), Assert.IsType<SolidFill>(shape.Line!.Fill).Color);
        Assert.Equal(3, shape.Line.Width, 0.01);
        var run = shape.Text!.Paragraphs[0].Inlines.OfType<HText>().First();
        Assert.Equal("Verdana", run.Format.Font);
        Assert.Equal("101010", run.Format.Color?.TrimStart('#'), ignoreCase: true);

        using var page = Draw(deck);
        AssertColor(new SKColor(0, 0, 0xFF), page, 100, 80);
    }

    [Fact]
    public void Variant_colours_follow_the_shape_s_or_the_page_s_variation()
    {
        // 100 = varColor1 of variation 0, through the page (65534); 101 = varColor2.
        var quick = Cell("QuickStyleFillColor", 100) + Cell("QuickStyleFillMatrix", 1) + Cell("QuickStyleLineMatrix", 3) + Cell("VariationColorIndex", 65534);
        var deck = Read(Save(Themed(quick,
            Shape(1, 1, 1.5, 1, 1, Rectangle(1, 1), OnTheme) +
            Shape(2, 3, 1.5, 1, 1, Rectangle(1, 1) + Cell("QuickStyleFillColor", 101), OnTheme),
            Cell("VariationColorIndex", 0))));
        var shapes = deck.Slides[0].Elements.OfType<SlideShape>().ToList();
        Assert.Equal(new SKColor(0x80, 0, 0x80), Assert.IsType<SolidFill>(shapes[0].Fill).Color);
        Assert.Equal(new SKColor(0, 0x80, 0x80), Assert.IsType<SolidFill>(shapes[1].Fill).Color);
        Assert.Null(shapes[0].Line); // the third line style has no line
    }

    [Fact]
    public void A_theme_fill_style_can_be_a_gradient_or_none()
    {
        var quick = Cell("QuickStyleFillColor", 3) + Cell("QuickStyleLineMatrix", 3);
        var deck = Read(Save(Themed(quick,
            Shape(1, 1, 1.5, 1.5, 1, Rectangle(1.5, 1) + Cell("QuickStyleFillMatrix", 2), OnTheme) +
            Shape(2, 3, 1.5, 1, 1, Rectangle(1, 1) + Cell("QuickStyleFillMatrix", 3) + TextBlock("No fill"), OnTheme))));
        var shapes = deck.Slides[0].Elements.OfType<SlideShape>().ToList();
        var gradient = Assert.IsType<GradientFill>(shapes[0].Fill);
        Assert.Equal(new SKColor(0xFF, 0x80, 0), gradient.Stops[0].Color);
        Assert.Equal(SKColors.White, gradient.Stops[^1].Color);
        Assert.Null(shapes[1].Fill);

        using var page = Draw(deck);
        AssertColor(new SKColor(0xFF, 0x80, 0), page, 22, 108); // the left end of the gradient
        AssertColor(SKColors.White, page, 124, 108, 60); // its right end
    }

    [Fact]
    public void Without_a_theme_themed_cells_keep_the_inherited_values()
    {
        var builder = new VsdxBuilder();
        builder.StyleSheets += ThemedStyle(Cell("QuickStyleFillColor", 2));
        builder.Page(Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1), OnTheme), 4, 3);
        var shape = Read(Save(builder)).Slides[0].Elements.OfType<SlideShape>().Single();
        Assert.Equal(SKColors.White, Assert.IsType<SolidFill>(shape.Fill).Color); // No Style's white
        Assert.Equal(SKColors.Black, Assert.IsType<SolidFill>(shape.Line!.Fill).Color);
    }

    // ───────────────────────── Gradients, shadows, text backgrounds, rounding ─────────────────────────

    [Fact]
    public void A_fill_gradient_section_is_drawn_along_its_angle()
    {
        var stops = "<Section N='FillGradient'>" +
                    "<Row IX='0'>" + Cell("GradientStopColor", "#FF0000") + Cell("GradientStopColorTrans", 0) + Cell("GradientStopPosition", 0) + "</Row>" +
                    "<Row IX='1'>" + Cell("GradientStopColor", "#0000FF") + Cell("GradientStopColorTrans", 0) + Cell("GradientStopPosition", 1) + "</Row></Section>";
        // 90°: from the top down.
        var deck = Read(Save(Page(Shape(1, 2, 1.5, 2, 2, Rectangle(2, 2) + Filled("#00FF00") + Cell("FillGradientEnabled", 1) +
            Cell("FillGradientDir", 0) + Cell("FillGradientAngle", Math.PI / 2) + stops))));
        var gradient = Assert.IsType<GradientFill>(deck.Slides[0].Elements.OfType<SlideShape>().Single().Fill);
        Assert.Equal(90, gradient.Angle, 0.01);
        using var page = Draw(deck);
        AssertColor(new SKColor(0xFF, 0, 0), page, 144, 40);
        AssertColor(new SKColor(0, 0, 0xFF), page, 144, 176);
    }

    private static VsdxBuilder Page(string shapes, string pageSheet = "")
    {
        var builder = new VsdxBuilder();
        builder.Page(shapes, 4, 3, pageSheet);
        return builder;
    }

    [Fact]
    public void A_shadow_is_drawn_under_the_shape_at_its_offset()
    {
        // A 1-inch square at (1, 1.5) with a grey shadow 0.25 inch right and down (the shape's own offset).
        var deck = Read(Save(Page(Shape(1, 1, 1.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000") + Cell("ShdwPattern", 1) +
            Cell("ShdwForegnd", "#404040") + Cell("ShapeShdwType", 1) + Cell("ShapeShdwOffsetX", 0.25) + Cell("ShapeShdwOffsetY", -0.25)))));
        var group = Assert.IsType<SlideGroup>(deck.Slides[0].Elements.Single());
        Assert.Equal(2, group.Children.Count);
        using var page = Draw(deck);
        AssertColor(new SKColor(0xFF, 0, 0), page, 72, 108); // the square
        AssertColor(new SKColor(0x40, 0x40, 0x40), page, 120, 156); // its shadow, past its bottom right corner
        AssertColor(SKColors.White, page, 30, 156); // nothing left of the shadow
    }

    [Fact]
    public void A_text_background_sits_behind_the_text()
    {
        var deck = Read(Save(Page(Shape(1, 2, 1.5, 2, 1, Geometry(Row("MoveTo", 1, ("X", 0), ("Y", 0.5)) + Row("LineTo", 2, ("X", 2), ("Y", 0.5))) +
            Cell("TextBkgnd", "#FFFF00") + TextBlock("Label")))));
        var group = Assert.IsType<SlideGroup>(deck.Slides[0].Elements.Single());
        var pieces = group.Children.Cast<SlideShape>().ToList();
        Assert.Equal(3, pieces.Count); // the line, the background, the text
        Assert.Equal(new SKColor(0xFF, 0xFF, 0), Assert.IsType<SolidFill>(pieces[1].Fill).Color);
        Assert.Equal("Label", string.Concat(pieces[2].Text!.Paragraphs[0].Inlines.OfType<HText>().Select(t => t.Text)));
        using var page = Draw(deck);
        AssertColor(new SKColor(0xFF, 0xFF, 0), page, 80, 100);
    }

    [Fact]
    public void Rounding_rounds_the_corners()
    {
        var deck = Read(Save(Page(Shape(1, 2, 1.5, 2, 2, Rectangle(2, 2) + Filled("#0000FF") + Cell("Rounding", 0.5)))));
        using var page = Draw(deck);
        AssertColor(SKColors.White, page, 74, 38); // the box's top left corner is cut off
        AssertColor(new SKColor(0, 0, 0xFF), page, 144, 40); // the top edge's middle is still there
        AssertColor(new SKColor(0, 0, 0xFF), page, 100, 80);
    }

    [Fact]
    public void Shapes_on_layers_that_do_not_print_are_left_out()
    {
        var layers = "<Section N='Layer'><Row IX='0'>" + Cell("Name", "Notes") + Cell("Visible", 1) + Cell("Print", 0) + "</Row>" +
                     "<Row IX='1'>" + Cell("Name", "Main") + Cell("Visible", 1) + Cell("Print", 1) + "</Row></Section>";
        var deck = Read(Save(Page(
            Shape(1, 1, 1.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000") + Cell("LayerMember", "0")) +
            Shape(2, 3, 1.5, 1, 1, Rectangle(1, 1) + Filled("#00FF00") + Cell("LayerMember", "1")), layers)));
        var shape = deck.Slides[0].Elements.OfType<SlideShape>().Single();
        Assert.Equal(new SKColor(0, 0xFF, 0), Assert.IsType<SolidFill>(shape.Fill).Color);
    }

    // ───────────────────────── The check ─────────────────────────

    [Fact]
    public void The_check_names_theme_effects_and_objects_without_a_picture()
    {
        var quick = Cell("QuickStyleFillColor", 2) + Cell("QuickStyleFillMatrix", 1) + Cell("QuickStyleLineMatrix", 1);
        var plain = Save(Themed(quick, Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1), OnTheme)));
        Assert.Null(VisioReader.FirstUnsupported(plain, Token));
        var effects = Save(Themed(quick, Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + Cell("QuickStyleEffectsMatrix", 2), OnTheme)));
        Assert.Equal("theme effects (shadows, glow, reflection)", VisioReader.FirstUnsupported(effects, Token));
        Assert.Null(VisioReader.FirstUnsupported(Save(Themed(quick, Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + Cell("QuickStyleEffectsMatrix", 1), OnTheme))), Token));

        // An embedded object whose data is no picture Filee draws.
        var builder = new VsdxBuilder();
        var media = builder.Media([1, 2, 3, 4], "bin");
        builder.Page(Shape(1, 2, 1.5, 1, 1, "<ForeignData ForeignType='Object'><Rel r:id='rId1' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'/></ForeignData>",
            "Type='Foreign'"), 4, 3, pictures: new() { ["rId1"] = media });
        Assert.Equal("embedded objects without a picture Filee can draw", VisioReader.FirstUnsupported(Save(builder), Token));
        // The drawing converter asks the same.
        Assert.Equal("embedded objects without a picture Filee can draw", new Drawings.DrawingConverter().FirstUnsupported(Save(builder), "vsd"));
    }

    [Fact]
    public void A_damaged_theme_is_read_as_far_as_it_goes()
    {
        var builder = new VsdxBuilder { Theme = "<?xml version='1.0'?><a:theme xmlns:a='" + DrawingMl + "'><a:themeElements/></a:theme>" };
        builder.StyleSheets += ThemedStyle(Cell("QuickStyleFillColor", 105) + Cell("QuickStyleFillMatrix", 103) + Cell("QuickStyleLineMatrix", 7) +
            Cell("VariationColorIndex", 3) + Cell("VariationStyleIndex", 9));
        builder.Page(Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + TextBlock("x"), OnTheme), 4, 3);
        var deck = Read(Save(builder));
        Assert.Single(deck.Slides[0].Elements);
    }
}
