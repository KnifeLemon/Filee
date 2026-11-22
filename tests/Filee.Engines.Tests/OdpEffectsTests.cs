// Charts, objects, 3D and effects in OpenDocument presentations and drawings without LibreOffice: embedded charts and
// formula objects drawn, OLE objects from their replacement pictures, 3D scenes as flat-shaded faces, shadows, glow,
// soft edges and extrusion read and drawn (OdfDrawReader, SlideRenderer); and PowerPoint decks with effects, 3D and
// charts written to ODP with them kept (OdpWriter), read back into the same model. Every file is built in code.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Charts;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OdpEffectsTests : IDisposable
{
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Dr3d = "urn:oasis:names:tc:opendocument:xmlns:dr3d:1.0";
    private static readonly XNamespace Loext = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-odp-fx-{Guid.NewGuid():N}");

    public OdpEffectsTests() => Directory.CreateDirectory(_folder);

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

    private const string GraphicDefault =
        "<style:default-style style:family=\"graphic\"><style:graphic-properties svg:stroke-color=\"#3465a4\" draw:fill-color=\"#729fcf\"/></style:default-style>";

    private string Save(OdpBuilder builder, string name = "deck.odp") => builder.Save(Path.Combine(_folder, $"{Guid.NewGuid():N}-{name}"));

    private static SlideDeck Read(string path) => OdfDrawReader.Read(path, cancellationToken: Token);

    private static SKBitmap Picture(SlideDeck deck, int slide = 0) => SlideOutput.ToBitmaps(deck, 72, [slide], Token).Single();

    private static bool Near(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance && Math.Abs(actual.Green - expected.Green) <= tolerance && Math.Abs(actual.Blue - expected.Blue) <= tolerance;

    private static int Count(SKBitmap bitmap, Func<SKColor, bool> test, SKRectI? area = null)
    {
        var box = area ?? new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        var n = 0;
        for (var y = Math.Max(0, box.Top); y < Math.Min(bitmap.Height, box.Bottom); y++)
            for (var x = Math.Max(0, box.Left); x < Math.Min(bitmap.Width, box.Right); x++)
                if (test(bitmap.GetPixel(x, y)))
                    n++;
        return n;
    }

    private static bool White(SKColor c) => c.Red > 245 && c.Green > 245 && c.Blue > 245;

    private static XElement Part(string package, string name)
    {
        using var zip = ZipFile.OpenRead(package);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream).Root!;
    }

    private static List<SlideElement> Leaves(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? Leaves(group.Children) : [e]).ToList();

    /// <summary>A deck of one page with these shapes, its chart object (Object 1) and the replacement picture of Object 2.</summary>
    private OdpBuilder Deck(string shapes, string contentStyles = "", bool drawing = false)
    {
        var builder = new OdpBuilder { Drawing = drawing, Styles = GraphicDefault, ContentStyles = contentStyles, Pages = OdpBuilder.Page(shapes) };
        builder.Parts["Object 1/content.xml"] = System.Text.Encoding.UTF8.GetBytes(OdfChartBuilder.Columns());
        builder.Parts["ObjectReplacements/Object 2"] = SlideDeckBuilder.TestPicture(40, 30);
        return builder;
    }

    private const string ChartFrame =
        "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"12cm\" svg:height=\"8cm\"><draw:object xlink:href=\"./Object 1\"/><draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>";

    // ───────────────────────── Charts and objects ─────────────────────────

    [Fact]
    public void An_embedded_chart_is_in_the_model_and_drawn_in_odp_and_odg()
    {
        foreach (var drawing in new[] { false, true })
        {
            var path = Save(Deck(ChartFrame, drawing: drawing), drawing ? "chart.odg" : "chart.odp");
            Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
            var deck = Read(path);
            var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>());
            Assert.Equal(ChartKind.Bar, shape.Chart?.Plots[0].Kind);
            Assert.Equal(3, shape.Chart!.Plots[0].Series.Count);
            using var picture = Picture(deck);
            // Red, green and blue columns in the frame.
            var frame = new SKRectI(28, 28, 28 + 340, 28 + 227);
            Assert.True(Count(picture, c => Near(c, new SKColor(0xC0, 0, 0)), frame) > 200);
            Assert.True(Count(picture, c => Near(c, new SKColor(0, 0x70, 0xC0)), frame) > 200);
        }
    }

    [Fact]
    public void An_ole_object_is_drawn_from_its_replacement_picture_and_one_without_a_picture_is_named()
    {
        var ole = "<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"6cm\"><draw:object-ole xlink:href=\"./Object 2\"/>" +
                  "<draw:image xlink:href=\"./ObjectReplacements/Object 2\"/></draw:frame>";
        var path = Save(Deck(ole));
        Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
        var deck = Read(path);
        var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>());
        Assert.True(shape.IsPicture);
        Assert.IsType<PictureFill>(shape.Fill);
        Assert.Contains(deck.LeftOut, p => p.Key.EndsWith(".objects_pictured", StringComparison.Ordinal));

        var bare = Save(Deck("<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"6cm\"><draw:object-ole xlink:href=\"./Object 3\"/></draw:frame>"));
        Assert.Equal("OLE objects", OdfDrawReader.FirstUnsupported(bare, Token));
        Assert.Contains(Read(bare).LeftOut, p => p.Key.EndsWith(".objects", StringComparison.Ordinal));
    }

    [Fact]
    public void A_formula_object_is_drawn_as_its_equation()
    {
        var builder = Deck("<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"3cm\"><draw:object xlink:href=\"./Object 4\"/></draw:frame>");
        builder.Parts["Object 4/content.xml"] = System.Text.Encoding.UTF8.GetBytes(
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\" display=\"block\"><semantics><mrow><msup><mi>x</mi><mn>2</mn></msup><mo>+</mo><mn>1</mn></mrow>" +
            "<annotation encoding=\"StarMath 5.0\">x^2 + 1</annotation></semantics></math>");
        var path = Save(builder);
        Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
        var shape = Assert.Single(Read(path).Slides[0].Elements.OfType<SlideShape>());
        Assert.IsType<HEquation>(Assert.Single(shape.Text!.Paragraphs[0].Inlines));
        using var picture = Picture(Read(path));
        // Centred in the object's box.
        Assert.True(Count(picture, c => !White(c), new SKRectI(57, 57, 284, 142)) > 20);
        Assert.Equal(0, Count(picture, c => !White(c), new SKRectI(0, 0, 57, 446)));
    }

    // ───────────────────────── 3D scenes ─────────────────────────

    /// <summary>A cube turned to show three faces, in a scene filling 6 × 6 cm at (2 cm, 2 cm), in perspective.</summary>
    private const string CubeScene =
        "<dr3d:scene draw:style-name=\"blue\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"6cm\" dr3d:vrp=\"(0 0 16885)\" dr3d:vpn=\"(0 0 1)\" dr3d:vup=\"(0 1 0)\" " +
        "dr3d:projection=\"perspective\" dr3d:ambient-color=\"#333333\">" +
        "<dr3d:light dr3d:diffuse-color=\"#cccccc\" dr3d:direction=\"(0.577 0.577 0.577)\" dr3d:enabled=\"true\"/>" +
        "<dr3d:cube draw:style-name=\"blue\" dr3d:transform=\"rotatey(35) rotatex(25)\" dr3d:min-edge=\"(-2500 -2500 -2500)\" dr3d:max-edge=\"(2500 2500 2500)\"/></dr3d:scene>";

    [Fact]
    public void A_3d_scene_is_drawn_as_shaded_faces_filling_its_box()
    {
        var path = Save(Deck(CubeScene, OdpBuilder.GraphicStyle("blue", "draw:fill=\"solid\" draw:fill-color=\"#2060c0\" draw:stroke=\"none\"")));
        Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
        var deck = Read(path);
        var group = Assert.IsType<SlideGroup>(Assert.Single(deck.Slides[0].Elements));
        var faces = group.Children.Cast<SlideShape>().ToList();
        Assert.InRange(faces.Count, 3, 6);
        // The projection fills the scene's box (10.5.2).
        var cm = 72 / 2.54f;
        Assert.Equal(2 * cm, faces.Min(f => f.Bounds.Left), 0.5);
        Assert.Equal(8 * cm, faces.Max(f => f.Bounds.Right), 0.5);
        Assert.Equal(2 * cm, faces.Min(f => f.Bounds.Top), 0.5);
        Assert.Equal(8 * cm, faces.Max(f => f.Bounds.Bottom), 0.5);
        // Shaded: the faces seen are lit differently.
        Assert.True(faces.Select(f => ((SolidFill)f.Fill!).Color).Distinct().Count() >= 2);
        using var picture = Picture(deck);
        Assert.True(Count(picture, c => !White(c), new SKRectI(57, 57, 227, 227)) > 15_000);
    }

    [Fact]
    public void Spheres_extrusions_and_rotation_objects_of_a_scene_are_drawn()
    {
        var scene = "<dr3d:scene svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"10cm\" svg:height=\"5cm\" dr3d:projection=\"parallel\">" +
                    "<dr3d:sphere draw:style-name=\"round\" dr3d:center=\"(-4000 0 0)\" dr3d:size=\"(3000 3000 3000)\"/>" +
                    "<dr3d:extrude draw:style-name=\"deep\" svg:viewBox=\"0 0 2000 2000\" svg:d=\"M0 0 L2000 0 L1000 2000 Z\" dr3d:transform=\"rotatey(30)\"/>" +
                    "<dr3d:rotate draw:style-name=\"round\" svg:viewBox=\"0 0 1000 2000\" svg:d=\"M0 0 L1000 0 L500 2000 L0 2000 Z\" dr3d:transform=\"translate(4000 0 0)\"/>" +
                    "</dr3d:scene>";
        var styles = OdpBuilder.GraphicStyle("round", "draw:fill-color=\"#c04020\" dr3d:horizontal-segments=\"12\" dr3d:vertical-segments=\"8\"") +
                     OdpBuilder.GraphicStyle("deep", "draw:fill-color=\"#20a040\" dr3d:depth=\"1cm\"");
        var deck = Read(Save(Deck(scene, styles)));
        var faces = Leaves(deck.Slides[0].Elements).Cast<SlideShape>().ToList();
        // The sphere: 12 × 8 segments; the triangle: front, back and two sides (its base is seen edge on); the lathe:
        // 12 steps of its slanted side (its flat top and bottom are seen edge on, so they show nothing).
        Assert.Equal(96 + 4 + 12, faces.Count);
        using var picture = Picture(deck);
        Assert.True(Count(picture, c => c.Red > c.Green + 40) > 500); // red sphere and lathe
        Assert.True(Count(picture, c => c.Green > c.Red + 40) > 100); // green extrusion
    }

    // ───────────────────────── Effects and extrusion ─────────────────────────

    [Fact]
    public void Shadow_glow_soft_edge_and_extrusion_are_read_and_drawn()
    {
        var styles = OdpBuilder.GraphicStyle("shadow", "draw:fill=\"solid\" draw:fill-color=\"#2050a0\" draw:stroke=\"none\" draw:shadow=\"visible\" " +
                                                        "draw:shadow-offset-x=\"0.5cm\" draw:shadow-offset-y=\"0.5cm\" draw:shadow-color=\"#000000\" draw:shadow-opacity=\"80%\" loext:shadow-blur=\"0.1cm\"") +
                     OdpBuilder.GraphicStyle("glow", "draw:fill=\"solid\" draw:fill-color=\"#ffffff\" draw:stroke=\"none\" loext:glow-radius=\"0.4cm\" loext:glow-color=\"#ff0000\" loext:glow-transparency=\"20%\" loext:softedge-radius=\"0.2cm\"") +
                     OdpBuilder.GraphicStyle("deep", "draw:fill=\"solid\" draw:fill-color=\"#20a040\" draw:stroke=\"none\" draw:secondary-fill-color=\"#806000\"");
        var shapes = "<draw:rect draw:style-name=\"shadow\" svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"4cm\" svg:height=\"3cm\"/>" +
                     "<draw:rect draw:style-name=\"glow\" svg:x=\"8cm\" svg:y=\"1cm\" svg:width=\"4cm\" svg:height=\"3cm\"/>" +
                     "<draw:custom-shape draw:style-name=\"deep\" svg:x=\"15cm\" svg:y=\"2cm\" svg:width=\"4cm\" svg:height=\"3cm\">" +
                     "<draw:enhanced-geometry draw:type=\"rectangle\" svg:viewBox=\"0 0 21600 21600\" draw:enhanced-path=\"M 0 0 L 21600 0 21600 21600 0 21600 Z N\" " +
                     "draw:extrusion=\"true\" draw:extrusion-depth=\"1cm 0\" draw:extrusion-rotation-angle=\"0 0\" draw:extrusion-color=\"true\"/></draw:custom-shape>";
        var builder = Deck(shapes, styles);
        var path = Save(builder);
        Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
        var deck = Read(path);
        var (shadowed, glowing, deep) = (deck.Slides[0].Elements[0] as SlideShape, deck.Slides[0].Elements[1] as SlideShape, deck.Slides[0].Elements[2] as SlideShape);
        var cm = 72 / 2.54f;
        var shadow = shadowed!.Effects!.Shadow!;
        Assert.Equal(0.5f * cm, shadow.OffsetX, 0.05);
        Assert.Equal(204, shadow.Color.Alpha);
        Assert.Equal(0.1f * cm, shadow.Blur, 0.05);
        Assert.Equal(0.4f * cm, glowing!.Effects!.Glow!.Radius, 0.05);
        Assert.Equal(new SKColor(0xFF, 0, 0, 204), glowing.Effects.Glow.Color);
        Assert.Equal(0.2f * cm, glowing.Effects.SoftEdge, 0.05);
        var three = deep!.ThreeD!;
        Assert.Equal(1 * cm, three.Depth, 0.05);
        Assert.Equal(new SKColor(0x80, 0x60, 0), three.Color);
        Assert.Equal(0.5f, three.SkewAmount); // the default skew: 50 45
        Assert.Equal(45, three.SkewAngle);

        using var picture = Picture(deck);
        // The shadow shows below and right of the shape; the glow is red around the white shape.
        Assert.True(Count(picture, c => c.Red < 120 && c.Green < 120 && c.Blue < 120, new SKRectI((int)(5 * cm), (int)(1.6 * cm), (int)(5.4 * cm), (int)(4.4 * cm))) > 20);
        Assert.True(Count(picture, c => c.Red > 200 && c.Green < 150, new SKRectI((int)(7.6 * cm), (int)(0.6 * cm), (int)(12.4 * cm), (int)(4.4 * cm))) > 100);
        // The extrusion's side, in its colour (shaded), up and right of the front face (skew 45°).
        Assert.True(Count(picture, c => c.Red > c.Blue + 20 && c.Green > c.Blue && c.Red > c.Green, new SKRectI((int)(15 * cm), (int)(1.2 * cm), (int)(19.6 * cm), (int)(2.2 * cm))) > 30);
    }

    // ───────────────────────── PPTX → ODP, ODP → ODP ─────────────────────────

    private string PowerPointDeck()
    {
        var deck = new SlideDeckBuilder();
        deck.Part("ppt/charts/chart1.xml", ChartBuilder.Columns(), "drawingml.chart");
        var chart = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Chart 3\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                    $"<p:xfrm><a:off x=\"{SlideDeckBuilder.E(460)}\" y=\"{SlideDeckBuilder.E(60)}\"/><a:ext cx=\"{SlideDeckBuilder.E(440)}\" cy=\"{SlideDeckBuilder.E(300)}\"/></p:xfrm>" +
                    $"<a:graphic><a:graphicData uri=\"{ChartBuilder.GraphicUri}\"><c:chart xmlns:c=\"{ChartBuilder.GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></p:graphicFrame>";
        var effects = "<a:effectLst><a:glow rad=\"101600\"><a:srgbClr val=\"FFC000\"><a:alpha val=\"60000\"/></a:srgbClr></a:glow>" +
                      "<a:outerShdw blurRad=\"50800\" dist=\"76200\" dir=\"2700000\"><a:srgbClr val=\"000000\"><a:alpha val=\"50000\"/></a:srgbClr></a:outerShdw>" +
                      "<a:reflection blurRad=\"6350\" stA=\"50000\" endA=\"300\" endPos=\"55000\" dist=\"12700\" dir=\"5400000\" sy=\"-100000\"/>" +
                      "<a:softEdge rad=\"25400\"/></a:effectLst>";
        var shadowed = deck.Shape("roundRect", 60, 60, 200, 120, fill: SlideDeckBuilder.Solid("#2050A0") + effects, styled: false);
        var cube = deck.Shape("rect", 120, 300, 160, 100, fill: SlideDeckBuilder.Solid("#C04020") +
            "<a:scene3d><a:camera prst=\"isometricOffAxis1Right\"><a:rot lat=\"1200000\" lon=\"19800000\" rev=\"0\"/></a:camera><a:lightRig rig=\"threePt\" dir=\"t\"/></a:scene3d>" +
            "<a:sp3d extrusionH=\"381000\"><a:extrusionClr><a:srgbClr val=\"602010\"/></a:extrusionClr></a:sp3d>", styled: false);
        deck.Slide(shadowed + cube + chart, relationships: [("rIdC", "chart", "../charts/chart1.xml")]);
        return deck.Save(Path.Combine(_folder, "effects.pptx"));
    }

    [Fact]
    public void A_pptx_with_shadow_glow_reflection_3d_and_a_chart_becomes_an_odp_that_keeps_them()
    {
        var pptx = PowerPointDeck();
        // The ODP writer takes the deck (3D is kept); the renderer still leaves 3D to LibreOffice where it is installed.
        Assert.Null(new OdpWriterConverter().FirstUnsupported(pptx, "pptx"));
        Assert.Equal("3D effects", new FileeRendererConverter().FirstUnsupported(pptx, "pptx"));

        var source = PptxSlideReader.Read(pptx, cancellationToken: Token);
        var shapes = source.Slides[0].Elements.OfType<SlideShape>().ToList();
        var shadowed = shapes[^3];
        Assert.Equal(8f, shadowed.Effects!.Glow!.Radius, 0.01);
        Assert.Equal(6f * MathF.Cos(MathF.PI / 4), shadowed.Effects.Shadow!.OffsetX, 0.01);
        Assert.Equal(0.55f, shadowed.Effects.Reflection!.EndPosition, 0.01);
        Assert.Equal(2f, shadowed.Effects.SoftEdge, 0.01);
        var cube = shapes[^2];
        Assert.Equal(30f, cube.ThreeD!.Depth, 0.01);
        Assert.Equal(20f, cube.ThreeD.RotationX, 0.01);
        Assert.Equal(30f, cube.ThreeD.RotationY, 0.01);

        var odp = Path.Combine(_folder, "effects.odp");
        OdpWriter.Write(source, odp, cancellationToken: Token);

        // The written XML: effects as attributes, the reflection as a faded copy, 3D as extrusion, the chart as an object.
        var content = Part(odp, "content.xml");
        var graphic = content.Descendants(Style + "graphic-properties").ToList();
        Assert.Contains(graphic, g => (string?)g.Attribute(Draw + "shadow") == "visible" && ((string?)g.Attribute(Draw + "shadow-opacity"))?.StartsWith("50", StringComparison.Ordinal) == true
                                      && g.Attribute(Loext + "shadow-blur") is not null);
        Assert.Contains(graphic, g => (string?)g.Attribute(Loext + "glow-radius") == "8pt" && (string?)g.Attribute(Loext + "glow-color") == "#ffc000");
        Assert.Contains(graphic, g => (string?)g.Attribute(Loext + "softedge-radius") == "2pt");
        Assert.Contains(graphic, g => g.Attribute(Draw + "opacity-name") is not null);
        Assert.Contains(graphic, g => (string?)g.Attribute(Draw + "secondary-fill-color") == "#602010");
        var geometry = content.Descendants(Draw + "enhanced-geometry").Single(g => g.Attribute(Draw + "extrusion") is not null);
        Assert.Equal("30pt 0", (string?)geometry.Attribute(Draw + "extrusion-depth"));
        Assert.Equal("-20 30", (string?)geometry.Attribute(Draw + "extrusion-rotation-angle"));
        Assert.Equal("true", (string?)geometry.Attribute(Draw + "extrusion-color"));
        Assert.NotNull(geometry.Attribute(Dr3d + "projection"));
        var objectFrame = content.Descendants(Draw + "object").Single();
        Assert.Equal("./Object 1", (string?)objectFrame.Attribute(XLink + "href"));
        Assert.Equal("./ObjectReplacements/Object 1", (string?)objectFrame.Parent!.Element(Draw + "image")!.Attribute(XLink + "href"));
        Assert.Contains(Part(odp, "META-INF/manifest.xml").Elements(), e => (string?)e.Attribute(Manifest + "full-path") == "Object 1/"
                                                                            && (string?)e.Attribute(Manifest + "media-type") == OdfChartWriter.MediaType);

        // Read back: the same chart, effects and 3D.
        Assert.Null(OdfDrawReader.FirstUnsupported(odp, Token));
        var back = Read(odp);
        var leaves = Leaves(back.Slides[0].Elements).OfType<SlideShape>().ToList();
        var chart = Assert.Single(leaves, s => s.Chart is not null).Chart!;
        Assert.Equal(ChartKind.Bar, chart.Plots[0].Kind);
        Assert.Equal(source.Slides[0].Elements.OfType<SlideShape>().Single(s => s.Chart is not null).Chart!.Plots[0].Series.Select(s => s.Values),
            chart.Plots.SelectMany(p => p.Series).Select(s => s.Values));
        var again = Assert.Single(leaves, s => s.Effects?.Glow is not null);
        Assert.Equal(shadowed.Effects.Glow.Color, again.Effects!.Glow!.Color);
        Assert.Equal(shadowed.Effects.Shadow.OffsetY, again.Effects.Shadow!.OffsetY, 0.05);
        Assert.Equal(2f, again.Effects.SoftEdge, 0.05);
        // The reflection is its faded copy under the shape.
        Assert.Contains(leaves, s => s.Effects is null && s.Bounds.Top >= again.Bounds.Bottom - 0.5f && Math.Abs(s.Bounds.Width - again.Bounds.Width) < 0.5f);
        var extruded = Assert.Single(leaves, s => s.ThreeD is not null).ThreeD!;
        Assert.Equal(cube.ThreeD.Depth, extruded.Depth, 0.05);
        Assert.Equal(cube.ThreeD.RotationX, extruded.RotationX, 0.05);
        Assert.Equal(cube.ThreeD.RotationY, extruded.RotationY, 0.05);
        Assert.Equal(cube.ThreeD.Color, extruded.Color);
    }

    [Fact]
    public void An_odp_with_a_chart_effects_an_object_and_a_scene_reads_back_the_same_after_the_writer()
    {
        var styles = OdpBuilder.GraphicStyle("shadow", "draw:fill=\"solid\" draw:fill-color=\"#2050a0\" draw:stroke=\"none\" draw:shadow=\"visible\" draw:shadow-offset-x=\"0.3cm\" draw:shadow-offset-y=\"0.2cm\"") +
                     OdpBuilder.GraphicStyle("blue", "draw:fill=\"solid\" draw:fill-color=\"#2060c0\" draw:stroke=\"none\"");
        var ole = "<draw:frame svg:x=\"14cm\" svg:y=\"9cm\" svg:width=\"4cm\" svg:height=\"3cm\"><draw:object-ole xlink:href=\"./Object 2\"/><draw:image xlink:href=\"./ObjectReplacements/Object 2\"/></draw:frame>";
        var source = Save(Deck(ChartFrame + "<draw:rect draw:style-name=\"shadow\" svg:x=\"14cm\" svg:y=\"1cm\" svg:width=\"4cm\" svg:height=\"3cm\"/>" + ole +
                               CubeScene.Replace("svg:x=\"2cm\" svg:y=\"2cm\"", "svg:x=\"20cm\" svg:y=\"1cm\"", StringComparison.Ordinal), styles));
        var first = Read(source);
        var odp = Path.Combine(_folder, "again.odp");
        OdpWriter.Write(first, odp, cancellationToken: Token);
        var second = Read(odp);
        Assert.Null(OdfDrawReader.FirstUnsupported(odp, Token));

        var (a, b) = (Leaves(first.Slides[0].Elements).OfType<SlideShape>().ToList(), Leaves(second.Slides[0].Elements).OfType<SlideShape>().ToList());
        Assert.Equal(a.Count, b.Count);
        Assert.NotNull(Assert.Single(b, s => s.Chart is not null).Chart);
        Assert.Equal(a.Single(s => s.Effects is not null).Effects!.Shadow, b.Single(s => s.Effects is not null).Effects!.Shadow);
        Assert.Equal(2, b.Count(s => s.IsPicture));
        // The scene's faces keep their places and colours.
        var faces = a.Where(s => s.Fill is SolidFill && !s.Paths[0].Stroke).ToList();
        foreach (var face in faces)
            Assert.Contains(b, s => Near(SlidePaint.Representative(s.Fill!), ((SolidFill)face.Fill!).Color, 2) && Math.Abs(s.Bounds.Left - face.Bounds.Left) < 0.5f);

        // And the same picture.
        using var before = Picture(first);
        using var after = Picture(second);
        var differ = 0;
        for (var y = 0; y < before.Height; y += 2)
            for (var x = 0; x < before.Width; x += 2)
                if (!Near(before.GetPixel(x, y), after.GetPixel(x, y), 60))
                    differ++;
        Assert.True(differ < before.Width * before.Height / 4 / 50, $"{differ} pixels differ");
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Hostile_scenes_are_cut_or_refused_through_the_document_limits()
    {
        await Task.Yield();
        // Segments far beyond what consumers support are cut to 256 each way.
        var huge = Save(Deck("<dr3d:scene svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"><dr3d:sphere draw:style-name=\"many\"/></dr3d:scene>",
            OdpBuilder.GraphicStyle("many", "dr3d:horizontal-segments=\"1000000000\" dr3d:vertical-segments=\"1000000000\"")));
        Assert.Equal(256 * 256, Leaves(Read(huge).Slides[0].Elements).Count);

        // Scenes nested past the budget's depth, and more cubes than a deck may draw, are refused.
        var nested = string.Concat(Enumerable.Repeat("<dr3d:scene svg:width=\"1cm\" svg:height=\"1cm\">", 100)) + "<dr3d:cube/>" + string.Concat(Enumerable.Repeat("</dr3d:scene>", 100));
        Assert.Throws<ConversionRefusedException>(() => Read(Save(Deck(nested))));
        var cubes = "<dr3d:scene svg:width=\"5cm\" svg:height=\"5cm\">" + string.Concat(Enumerable.Repeat("<dr3d:cube/>", 60_000)) + "</dr3d:scene>";
        Assert.Throws<ConversionRefusedException>(() => Read(Save(Deck(cubes))));
    }
}
