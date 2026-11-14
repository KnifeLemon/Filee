// Filee's Visio reader (Visio/): drawings written in code (VsdxBuilder) read into the slide model and drawn by the
// slide renderer; positions are checked on the model and on the pixels of the page drawn at 72 dpi (a pixel a point).
// Pages are 4 × 3 inches (288 × 216 points) unless a test says otherwise; Visio's y runs up from the page's bottom.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Slides;
using Filee.Engines.Visio;
using SkiaSharp;
using static Filee.Engines.Tests.VsdxBuilder;

namespace Filee.Engines.Tests;

public sealed class VisioTests(EngineFixture fx) : IDisposable, IClassFixture<EngineFixture>
{
    private readonly string _folder = fx.NewFolder();

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

    private SlideDeck Read(VsdxBuilder builder, string name = "drawing.vsdx") => VisioReader.Read(builder.Save(Path.Combine(_folder, name)), Token);

    /// <summary>One page of <paramref name="shapes"/>, 4 × 3 inches.</summary>
    private SlideDeck Page(string shapes)
    {
        var builder = new VsdxBuilder();
        builder.Page(shapes, 4, 3);
        return Read(builder);
    }

    /// <summary>The page drawn at 72 dpi.</summary>
    private static SKBitmap Draw(SlideDeck deck, int page = 0) => Assert.Single(SlideOutput.ToBitmaps(deck, 72, [page], Token));

    private static void AssertColor(SKColor expected, SKBitmap bitmap, int x, int y)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(Math.Abs(actual.Red - expected.Red) < 40 && Math.Abs(actual.Green - expected.Green) < 40 && Math.Abs(actual.Blue - expected.Blue) < 40,
            $"pixel ({x}, {y}) is {actual}, expected {expected}");
    }

    private static readonly SKColor Red = new(0xFF, 0, 0), Green = new(0, 0xFF, 0), Blue = new(0, 0, 0xFF), White = SKColors.White;

    // ───────────────────────── Shapes and transforms ─────────────────────────

    [Fact]
    public void Rectangles_sit_at_their_pin_with_y_up_and_the_page_size_is_the_slide_size()
    {
        var deck = Page(
            Shape(1, 1, 2, 1, 0.5, Rectangle(1, 0.5) + Filled("#FF0000")) +
            // Pinned at its bottom-left corner.
            Shape(2, 3, 1, 0.5, 0.5, Rectangle(0.5, 0.5) + Filled("#0000FF"), locPinX: 0, locPinY: 0));
        Assert.Equal(288, deck.Width, 0.01);
        Assert.Equal(216, deck.Height, 0.01);
        var shapes = Assert.Single(deck.Slides).Elements.Cast<SlideShape>().ToList();
        Assert.Equal(new SKRect(36, 54, 108, 90), shapes[0].Bounds);
        Assert.Equal(new SKRect(216, 108, 252, 144), shapes[1].Bounds);

        using var page = Draw(deck);
        AssertColor(Red, page, 72, 72);
        AssertColor(White, page, 72, 100);
        AssertColor(Blue, page, 234, 126);
        AssertColor(White, page, 200, 126);
    }

    [Fact]
    public void An_ellipse_row_draws_an_ellipse()
    {
        var deck = Page(Shape(1, 2, 1.5, 2, 1, Geometry(Row("Ellipse", 1, ("X", 1), ("Y", 0.5), ("A", 2), ("B", 0.5), ("C", 1), ("D", 1))) + Filled("#00FF00")));
        using var page = Draw(deck);
        AssertColor(Green, page, 144, 108);
        AssertColor(Green, page, 210, 108);
        // The box's corners are outside the ellipse.
        AssertColor(White, page, 76, 76);
        AssertColor(White, page, 212, 140);
    }

    [Fact]
    public void Shapes_turn_about_their_pin_and_mirror_about_their_local_pin()
    {
        var triangle = Geometry(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", 1), ("Y", 0)) + Row("LineTo", 3, ("X", 0), ("Y", 1)) +
                                Row("LineTo", 4, ("X", 0), ("Y", 0)));
        var deck = Page(
            // 2 × 0.5 turned a quarter: a bar 0.5 wide and 2 high about (2, 1.5).
            Shape(1, 2, 1.5, 2, 0.5, Rectangle(2, 0.5) + Filled("#FF0000") + Cell("Angle", Math.PI / 2)) +
            // Turned a quarter about its bottom-left corner at (3.5, 1): it now reaches up and to the left.
            Shape(2, 3.5, 1, 1, 0.5, Rectangle(1, 0.5) + Filled("#0000FF") + Cell("Angle", Math.PI / 2), locPinX: 0, locPinY: 0) +
            // A triangle filling the bottom-left half, mirrored: the bottom-right half.
            Shape(3, 0.5, 0.5, 1, 1, triangle + Filled("#00FF00") + Cell("FlipX", 1)));
        using var page = Draw(deck);
        AssertColor(Red, page, 144, 50);
        AssertColor(Red, page, 144, 170);
        AssertColor(White, page, 90, 108);

        AssertColor(Blue, page, 243, 108);
        AssertColor(White, page, 270, 126);

        AssertColor(Green, page, 66, 210);
        AssertColor(White, page, 6, 190);
        var flipped = deck.Slides[0].Elements[2];
        Assert.True(flipped.FlipHorizontal);
        Assert.False(flipped.FlipVertical);
        Assert.Equal(-90, deck.Slides[0].Elements[0].Rotation, 0.001);
    }

    // ───────────────────────── Inheritance ─────────────────────────

    [Fact]
    public void Shapes_inherit_geometry_cells_and_text_from_their_master_scaled_to_their_size()
    {
        var builder = new VsdxBuilder();
        var master = builder.Master(Shape(5, 0.5, 0.5, 1, 1, Rectangle(1, 1) + Filled("#0000FF") + TextBlock("M")));
        builder.Page(
            // Only the place and the width are its own: the 1 × 1 master's rectangle is stretched to 2 × 1.
            $"<Shape ID='1' Type='Shape' Master='{master}'>" + Cell("PinX", 2) + Cell("PinY", 1.5) + Cell("Width", 2) + "</Shape>" +
            // Its geometry deleted: nothing but the inherited text.
            $"<Shape ID='2' Type='Shape' Master='{master}'>" + Cell("PinX", 3.5) + Cell("PinY", 0.5) + "<Section N='Geometry' IX='0' Del='1'/></Shape>",
            4, 3);
        var deck = Read(builder);
        var shapes = deck.Slides[0].Elements.Cast<SlideShape>().ToList();
        Assert.Equal(new SKRect(72, 72, 216, 144), shapes[0].Bounds);
        Assert.Equal("M", TextOf(shapes[0]));
        Assert.Null(shapes[1].Fill);
        Assert.Equal("M", TextOf(shapes[1]));

        using var page = Draw(deck);
        AssertColor(Blue, page, 210, 108);
        AssertColor(Blue, page, 78, 108);
        AssertColor(White, page, 282, 210);
    }

    [Fact]
    public void Line_fill_and_text_cells_come_from_the_style_sheets_along_their_parents()
    {
        var builder = new VsdxBuilder();
        builder.StyleSheets += "<StyleSheet ID='1' NameU='Green' LineStyle='0' FillStyle='0' TextStyle='0'>" + Cell("FillForegnd", "#00FF00") +
                               Cell("LinePattern", 0) + "</StyleSheet>" +
                               "<StyleSheet ID='2' NameU='Child' LineStyle='1' FillStyle='1' TextStyle='1'>" +
                               "<Section N='Character'><Row IX='0'>" + Cell("Color", "2") + Cell("Size", 0.25) + "</Row></Section></StyleSheet>";
        builder.Page(Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + TextBlock("Styled"), "Type='Shape' LineStyle='2' FillStyle='2' TextStyle='2'"), 4, 3);
        var shape = (SlideShape)Read(builder).Slides[0].Elements[0];
        Assert.Equal(new SolidFill(Green), shape.Fill);
        Assert.Null(shape.Line);
        var run = shape.Text!.Paragraphs[0].Inlines.OfType<HText>().Single();
        // Colour 2 of the document's table, 0.25 in = 18 pt, the font from "No Style".
        Assert.Equal("#FF0000", run.Format.Color);
        Assert.Equal(1800, run.Format.Size);
        Assert.Equal("Calibri", run.Format.Font);
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public void Text_takes_its_character_and_paragraph_rows_and_sits_in_its_text_block()
    {
        var body = Rectangle(2, 1) + Cell("LinePattern", 0) + Cell("FillPattern", 0) +
                   Cell("TxtPinX", 1) + Cell("TxtPinY", 0) + Cell("TxtWidth", 1) + Cell("TxtHeight", 0.5) + Cell("TxtLocPinX", 0.5) + Cell("TxtLocPinY", 0.25) +
                   "<Section N='Character'><Row IX='0'>" + Cell("Color", "#FF0000") + Cell("Size", 0.25) + Cell("Style", 1) + "</Row>" +
                   "<Row IX='1'>" + Cell("Color", "#0000FF") + "</Row></Section>" +
                   "<Section N='Paragraph'><Row IX='0'>" + Cell("HorzAlign", 0) + "</Row></Section>" +
                   TextBlock("<cp IX='0'/><pp IX='0'/>Hello<cp IX='1'/> World\nSecond\n");
        var deck = Page(Shape(1, 2, 1.5, 2, 1, body));
        var shape = (SlideShape)deck.Slides[0].Elements[0];
        var frame = shape.Text!;
        Assert.Equal(2, frame.Paragraphs.Count);
        var runs = frame.Paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.Equal(["Hello", " World"], runs.Select(r => r.Text));
        Assert.Equal(("#FF0000", true, 1800), (runs[0].Format.Color, runs[0].Format.Bold == true, runs[0].Format.Size!.Value));
        Assert.Equal(("#0000FF", 1200), (runs[1].Format.Color, runs[1].Format.Size!.Value));
        Assert.Equal(HAlign.Left, frame.Paragraphs[0].Format.Align);
        // The text block's centre at (1, 0) of the shape: half below the box.
        Assert.Equal(new SKRect(36, 54, 108, 90), frame.Area);
        Assert.Equal(TextAnchor.Middle, frame.Anchor);

        // The red letters are drawn about the bottom edge of the box (y = 144 on the page).
        using var page = Draw(deck);
        var red = 0;
        for (var y = 120; y < 170; y++)
            for (var x = 72; x < 216; x++)
                if (page.GetPixel(x, y) is { Red: > 180, Green: < 90, Blue: < 90 })
                    red++;
        Assert.True(red > 20, $"{red} red pixels");
    }

    // ───────────────────────── Groups, connectors, pictures ─────────────────────────

    [Fact]
    public void Group_members_are_placed_in_the_group_and_turn_with_it()
    {
        string Group(string attributes) =>
            Shape(1, 2, 1.5, 2, 1, "<Shapes>" +
                                   Shape(2, 0.5, 0.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000")) +
                                   Shape(3, 1.5, 0.5, 1, 1, Rectangle(1, 1) + Filled("#0000FF")) + "</Shapes>" + attributes, "Type='Group'");
        var builder = new VsdxBuilder();
        builder.Page(Group(""), 4, 3);
        builder.Page(Group(Cell("FlipX", 1)), 4, 3);
        var deck = Read(builder);
        var group = Assert.IsType<SlideGroup>(deck.Slides[0].Elements[0]);
        Assert.Equal(new SKRect(72, 72, 216, 144), group.Bounds);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal(new SKRect(0, 0, 72, 72), group.Children[0].Bounds);

        using (var page = Draw(deck))
        {
            AssertColor(Red, page, 108, 108);
            AssertColor(Blue, page, 180, 108);
        }
        using (var mirrored = Draw(deck, 1))
        {
            AssertColor(Blue, mirrored, 108, 108);
            AssertColor(Red, mirrored, 180, 108);
        }
    }

    [Fact]
    public void Groups_nested_too_deeply_are_refused()
    {
        var shapes = Shape(99, 0.5, 0.5, 1, 1, Rectangle(1, 1));
        for (var i = 0; i < 40; i++)
            shapes = Shape(100 + i, 0.5, 0.5, 1, 1, $"<Shapes>{shapes}</Shapes>", "Type='Group'");
        var refused = Assert.Throws<ConversionRefusedException>(() => Page(shapes));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void A_connector_is_drawn_as_its_geometry_with_its_arrowhead()
    {
        var line = Geometry(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", 2), ("Y", 0)), cells: Cell("NoFill", 1));
        var deck = Page(Shape(1, 2, 1.5, 2, 0, line + Cell("LineWeight", 0.05) + Cell("LineColor", "#FF0000") + Cell("EndArrow", 4) +
                                                  Cell("BeginX", 1) + Cell("BeginY", 1.5) + Cell("EndX", 3) + Cell("EndY", 1.5)));
        var shape = (SlideShape)deck.Slides[0].Elements[0];
        Assert.Null(shape.Fill);
        Assert.Equal(3.6f, shape.Line!.Width, 0.01f);
        Assert.Equal(LineEndKind.Triangle, shape.Line.Tail.Kind);
        Assert.Equal(LineEndKind.None, shape.Line.Head.Kind);
        using var page = Draw(deck);
        AssertColor(Red, page, 144, 108);
        AssertColor(White, page, 144, 120);
    }

    [Fact]
    public void A_foreign_shape_shows_its_picture()
    {
        using var bitmap = new SKBitmap(20, 10);
        bitmap.Erase(new SKColor(0xFF, 0x00, 0xFF));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var builder = new VsdxBuilder();
        var media = builder.Media(png.ToArray());
        builder.Page(Shape(1, 2, 1.5, 2, 1, Cell("ImgOffsetX", 0) + Cell("ImgOffsetY", 0) + Cell("ImgWidth", 1) + Cell("ImgHeight", 1) +
                                            "<ForeignData ForeignType='Bitmap' CompressionType='PNG'><Rel r:id='rId1' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'/></ForeignData>",
                "Type='Foreign'"),
            4, 3, pictures: new Dictionary<string, string> { ["rId1"] = media });
        var deck = Read(builder);
        var shape = (SlideShape)deck.Slides[0].Elements[0];
        Assert.True(shape.IsPicture);
        var fill = Assert.IsType<PictureFill>(shape.Fill);
        // The image rectangle is the left half of the box.
        Assert.Equal(new SKRect(0, 0, 0.5f, 0), fill.Stretch);
        using var page = Draw(deck);
        AssertColor(new SKColor(0xFF, 0x00, 0xFF), page, 108, 108);
        AssertColor(White, page, 180, 108);
    }

    // ───────────────────────── Geometry rows ─────────────────────────

    /// <summary>The bounds of the shape's outline in local inches (y up) of a 1 × 1 shape at the page's bottom-left.</summary>
    private RectangleInches Outline(string rows)
    {
        var deck = Page(Shape(1, 0.5, 0.5, 1, 1, Geometry(rows) + Cell("FillPattern", 0)));
        var shape = (SlideShape)deck.Slides[0].Elements[0];
        var bounds = shape.Paths.Single(p => p.Stroke).Path.TightBounds;
        return new RectangleInches(bounds.Left / 72, 1 - bounds.Bottom / 72, bounds.Right / 72, 1 - bounds.Top / 72);
    }

    private readonly record struct RectangleInches(double Left, double Bottom, double Right, double Top);

    [Fact]
    public void Arcs_bow_counterclockwise_for_a_positive_bow_and_elliptical_arcs_pass_their_control_point()
    {
        // From (0, 0) to (1, 0) with a bow of 0.5: the half circle below the chord.
        var arc = Outline(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("ArcTo", 2, ("X", 1), ("Y", 0), ("A", 0.5)));
        Assert.Equal(-0.5, arc.Bottom, 0.01);
        Assert.Equal(0, arc.Top, 0.01);
        Assert.Equal(1, arc.Right, 0.01);
        var above = Outline(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("ArcTo", 2, ("X", 1), ("Y", 0), ("A", -0.25)));
        Assert.Equal(0.25, above.Top, 0.01);

        // Half an ellipse twice as wide as high, through its top.
        var ellipse = Outline(Row("MoveTo", 1, ("X", 0), ("Y", 0)) +
                              Row("EllipticalArcTo", 2, ("X", 1), ("Y", 0), ("A", 0.5), ("B", 0.25), ("C", 0), ("D", 2)));
        Assert.Equal(0.25, ellipse.Top, 0.01);
        Assert.Equal(0, ellipse.Bottom, 0.01);
    }

    [Fact]
    public void Nurbs_polylines_and_relative_curves_are_drawn_through_their_data()
    {
        // A cubic NURBS clamped at both ends is the Bézier of its control points: (0,0) (0,1) (1,1) (1,0) peaks at 0.75.
        var nurbs = Outline(Row("MoveTo", 1, ("X", 0), ("Y", 0)) +
                            "<Row T='NURBSTo' IX='2'>" + Cell("X", 1) + Cell("Y", 0) + Cell("A", 1) + Cell("B", 1) + Cell("C", 0) + Cell("D", 1) +
                            "<Cell N='E' V='0' F='NURBS(1, 3, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1)'/></Row>");
        Assert.Equal(0.75, nurbs.Top, 0.01);
        Assert.Equal(1, nurbs.Right, 0.01);

        // Polyline points as fractions of the shape's size.
        var polyline = Outline(Row("MoveTo", 1, ("X", 0), ("Y", 0)) +
                               "<Row T='PolylineTo' IX='2'>" + Cell("X", 1) + Cell("Y", 0) + "<Cell N='A' V='0' F='POLYLINE(0, 0, 0.5, 0.8)'/></Row>");
        Assert.Equal(0.8, polyline.Top, 0.01);

        var bezier = Outline(Row("RelMoveTo", 1, ("X", 0), ("Y", 0)) +
                             Row("RelCubBezTo", 2, ("X", 1), ("Y", 0), ("A", 0), ("B", 1), ("C", 1), ("D", 1)));
        Assert.Equal(0.75, bezier.Top, 0.01);
    }

    [Fact]
    public void Sections_with_no_fill_or_no_line_and_holes_are_kept_apart()
    {
        var outer = Geometry(Row("Ellipse", 1, ("X", 1), ("Y", 0.5), ("A", 2), ("B", 0.5), ("C", 1), ("D", 1)), 0);
        var hole = Geometry(Row("Ellipse", 1, ("X", 1), ("Y", 0.5), ("A", 1.2), ("B", 0.5), ("C", 1), ("D", 0.7)), 1);
        var hidden = Geometry(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", 2), ("Y", 1)), 2, Cell("NoShow", 1));
        var deck = Page(Shape(1, 2, 1.5, 2, 1, outer + hole + hidden + Filled("#0000FF")));
        using var page = Draw(deck);
        AssertColor(Blue, page, 85, 108);
        // The inner ellipse is a hole.
        AssertColor(White, page, 144, 108);
    }

    // ───────────────────────── Pages ─────────────────────────

    [Fact]
    public void Background_pages_are_drawn_under_their_pages_and_hidden_layers_are_left_out()
    {
        var builder = new VsdxBuilder();
        var background = builder.Page(Shape(1, 1, 1.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000")), 4, 3, background: true);
        var layers = "<Section N='Layer'><Row IX='0'>" + Cell("Name", "Hidden") + Cell("Visible", 0) + "</Row>" +
                     "<Row IX='1'>" + Cell("Name", "Shown") + Cell("Visible", 1) + "</Row></Section>";
        builder.Page(Shape(1, 3, 1.5, 1, 1, Rectangle(1, 1) + Filled("#0000FF") + Cell("LayerMember", "1")) +
                     Shape(2, 2, 0.5, 1, 1, Rectangle(1, 1) + Filled("#00FF00") + Cell("LayerMember", "0")),
            4, 3, layers, backPage: background);
        var deck = Read(builder);
        var slide = Assert.Single(deck.Slides);
        Assert.Equal(2, slide.Elements.Count);
        using var page = Draw(deck);
        AssertColor(Red, page, 72, 108);
        AssertColor(Blue, page, 216, 108);
        AssertColor(White, page, 144, 180);
    }

    [Fact]
    public void A_smaller_page_sits_at_the_top_of_the_largest_and_a_drawing_scale_scales_the_page()
    {
        var builder = new VsdxBuilder();
        builder.Page(Shape(1, 0.5, 0.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000")), 4, 3);
        builder.Page(Shape(1, 0.5, 0.5, 1, 1, Rectangle(1, 1) + Filled("#FF0000")), 2, 2);
        // 1 inch on paper is 2 inches of drawing: an 8 × 6 drawing on a 4 × 3 page.
        builder.Page(Shape(1, 1, 1, 2, 2, Rectangle(2, 2) + Filled("#0000FF")), 8, 6, Cell("PageScale", 1) + Cell("DrawingScale", 2));
        var deck = Read(builder);
        Assert.Equal((288f, 216f), (deck.Width, deck.Height));
        Assert.Equal(new SKRect(0, 72, 72, 144), deck.Slides[1].Elements[0].Bounds);
        Assert.Equal(new SKRect(0, 144, 72, 216), deck.Slides[2].Elements[0].Bounds);
    }

    // ───────────────────────── Converter and hostile input ─────────────────────────

    [Fact]
    public void The_drawing_converter_reads_vsdx_and_leaves_binary_vsd_to_libreoffice()
    {
        var converter = new DrawingConverter();
        Assert.Equal([("vsd", "pdf"), ("vsd", "svg"), ("vsd", "png")], converter.Edges.Select(e => (e.From, e.To)));
        Assert.All(converter.Edges, e => Assert.True(e.Checked));

        var binary = Path.Combine(_folder, "old.vsd");
        File.WriteAllBytes(binary, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        Assert.Equal("binary Visio (VSD)", converter.FirstUnsupported(binary, "vsd"));
        Assert.Equal("error.unsupported_source", Assert.Throws<ConversionRefusedException>(() => VisioReader.Read(binary, Token)).ErrorKey);

        var builder = new VsdxBuilder();
        builder.Page(Shape(1, 1, 1, 1, 1, Rectangle(1, 1)), 4, 3);
        Assert.Null(converter.FirstUnsupported(builder.Save(Path.Combine(_folder, "new.vsdx")), "vsd"));
    }

    [Fact]
    public void Vsdx_routes_go_through_the_drawing_converter_and_binary_vsd_ones_keep_libreoffice()
    {
        var planner = fx.Catalog.CreatePlanner(["libreoffice"]);
        foreach (var target in new[] { "pdf", "svg", "png" })
        {
            Assert.Equal("drawing", planner.Plan("vsd", target)!.Steps[0].Converter.Id);
            Assert.Equal("libreoffice", planner.Plan("vsd", target, withoutChecked: true)!.Steps[0].Converter.Id);
        }
    }

    [Theory]
    [InlineData("pdf", 1)]
    [InlineData("svg", 2)]
    [InlineData("png", 2)]
    public async Task A_vsdx_is_converted_by_the_drawing_converter(string target, int files)
    {
        var builder = new VsdxBuilder();
        builder.Page(Shape(1, 1, 1, 1, 1, Rectangle(1, 1) + Filled("#FF0000") + TextBlock("One")), 4, 3);
        builder.Page(Shape(1, 2, 1, 1, 1, Rectangle(1, 1) + Filled("#0000FF") + TextBlock("Two")), 4, 3);
        var source = builder.Save(Path.Combine(_folder, "two.vsdx"));
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "vsd", target, new Preset(), new FolderOutput(_folder), _folder), null, Token);
        Assert.Equal(files, written.Count);
        Assert.All(written, path => Assert.True(new FileInfo(path).Length > 100));
    }

    [Fact]
    public void Master_and_style_loops_are_cut_and_end()
    {
        var builder = new VsdxBuilder();
        builder.StyleSheets += "<StyleSheet ID='1' LineStyle='2' FillStyle='2' TextStyle='2'/><StyleSheet ID='2' LineStyle='1' FillStyle='1' TextStyle='1'/>";
        // A master shape that is an instance of its own master.
        var master = builder.Master($"<Shape ID='5' Type='Shape' Master='1' MasterShape='5'>" + Cell("Width", 1) + Cell("Height", 1) + Rectangle(1, 1) + "</Shape>");
        var background = builder.Page(Shape(1, 1, 1, 1, 1, Rectangle(1, 1)), 4, 3, background: true, backPage: 0);
        builder.Page($"<Shape ID='1' Type='Shape' Master='{master}' LineStyle='1' FillStyle='1' TextStyle='1'>" + Cell("PinX", 2) + Cell("PinY", 1) + "</Shape>",
            4, 3, backPage: background);
        var deck = Read(builder);
        Assert.Single(deck.Slides);
        Assert.Equal(2, deck.Slides[0].Elements.Count);
    }

    [Fact]
    public void Huge_nurbs_and_sections_are_refused()
    {
        var points = string.Join(", ", Enumerable.Repeat("0.5, 0.5, 0, 1", VisioGeometry.MaxSplinePoints + 1));
        var nurbs = Row("MoveTo", 1, ("X", 0), ("Y", 0)) +
                    "<Row T='NURBSTo' IX='2'>" + Cell("X", 1) + Cell("Y", 0) + $"<Cell N='E' V='0' F='NURBS(1, 3, 0, 0, {points})'/></Row>";
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Page(Shape(1, 1, 1, 1, 1, Geometry(nurbs)))).ErrorKey);

        var rows = string.Concat(Enumerable.Range(1, VisioSheet.MaxRows + 1).Select(i => Row("LineTo", i, ("X", i % 2), ("Y", 0))));
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Page(Shape(1, 1, 1, 1, 1, Geometry(rows)))).ErrorKey);
    }

    /// <summary>Hands out <c>out{suffix}.{ext}</c> paths in a folder.</summary>
    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }

    [Fact]
    public void Text_markers_naming_missing_rows_take_the_first_row()
    {
        var markers = string.Concat(Enumerable.Range(1, 20_000).Select(i => $"<cp IX='{i}'/><pp IX='{i}'/>a" + "\n"));
        var deck = Page(Shape(1, 2, 1.5, 2, 1, Rectangle(2, 1) + "<Section N='Character'><Row IX='0'>" + Cell("Color", "#FF0000") + "</Row></Section>" +
                                              TextBlock(markers)));
        var paragraphs = ((SlideShape)deck.Slides[0].Elements[0]).Text!.Paragraphs;
        Assert.Equal(20_000, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.Equal("#FF0000", p.Inlines.OfType<HText>().Single().Format.Color));
    }

    private static string TextOf(SlideShape shape) =>
        string.Concat(shape.Text?.Paragraphs.SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text) ?? []);
}
