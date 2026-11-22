// Bent and curved connectors in the document model (HShapeKind.Path, HGeometry.Path): Word's connector presets
// (wps bentConnector / curvedConnector with adjust values, VML types 33 to 40) and free-form lines and shapes, ODF
// draw:connector (svg:d, or its draw:type without one), draw:path, draw:polyline and draw:polygon, 한글
// hp:connectLine STROKE / ARC and hp:polygon, RTF shape types 33 to 40 and free-forms, and a slide's p:cxnSp with its
// arrowheads, all built in code, are read as paths (closed ones filled); a model built in code goes through the DOCX,
// RTF, ODT, HWPX and HWP writers and back; connector presets go back to DOCX as presets; the renderer draws the path,
// and the slide reader draws ODF connectors along their route.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class ConnectorTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly HAnchor OnPaper = new("PAPER", "LEFT", 10000, "PAPER", "TOP", 10000, "IN_FRONT_OF_TEXT", true);

    private static HShape Open(HPath path, int width, int height, HArrow? head, HArrow? tail, int offset = 0) => new(HShapeKind.Path)
    {
        Width = width,
        Height = height,
        Anchor = OnPaper with { VerticalOffset = OnPaper.VerticalOffset + offset },
        Geometry = new HGeometry(width, height, Path: path),
        Line = new HBorder(HBorderStyle.Solid, 0.5, "#1F4E79"),
        Head = head,
        Tail = tail,
    };

    private static HPoint Pt(int x, int y) => new(x, y);

    /// <summary>Right angles, as bentConnector3 draws them.</summary>
    private static readonly HPath Bent = new(Pt(0, 0), [new(Pt(5000, 0)), new(Pt(5000, 8000)), new(Pt(20000, 8000))]);

    /// <summary>Two cubics joined halfway between their controls, as curvedConnector3 draws them.</summary>
    private static readonly HPath Curved = new(Pt(0, 0),
        [new(Pt(5000, 4000), Pt(2500, 0), Pt(5000, 2000)), new(Pt(20000, 8000), Pt(5000, 6000), Pt(12500, 8000))]);

    /// <summary>A straight piece and a curve: no 한글 curved connector says it.</summary>
    private static readonly HPath Mixed = new(Pt(0, 8000), [new(Pt(8000, 8000)), new(Pt(20000, 0), Pt(14000, 8000), Pt(20000, 4000))]);

    private static HShape[] Connectors() =>
    [
        Open(Bent, 20000, 8000, null, new HArrow(HArrowKind.Triangle)),
        Open(Curved, 20000, 8000, new HArrow(HArrowKind.Oval, HArrowSize.Small, HArrowSize.Small), new HArrow(HArrowKind.Stealth, HArrowSize.Large, HArrowSize.Large), offset: 10000),
    ];

    private static HDocument Model(params HShape[] shapes)
    {
        var document = new HDocument();
        var section = new HSection();
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(shapes);
        paragraph.Inlines.Add(new HText("connectors", default));
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);
        return document;
    }

    private static List<HShape> Shapes(HDocument document) =>
        [.. document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HShape>()];

    /// <summary>The path's points measured in the shape's size, and its pieces (L straight, C curved).</summary>
    private static (List<(double X, double Y)> Points, string Pieces) Measured(HShape shape)
    {
        var geometry = shape.Geometry!;
        var path = geometry.Path!;
        var points = OpenPaths.Points(path).Select(p => (
            geometry.Width > 0 ? p.X * (double)shape.Width / geometry.Width : p.X,
            geometry.Height > 0 ? p.Y * (double)shape.Height / geometry.Height : p.Y)).ToList();
        return (points, string.Concat(path.Segments.Select(s => s.Curved ? 'C' : 'L')));
    }

    private static void AssertPath(HShape expected, HShape read, double tolerance = 20)
    {
        Assert.Equal(HShapeKind.Path, read.Kind);
        Assert.Equal((expected.Head, expected.Tail), (read.Head, read.Tail));
        Assert.InRange(read.Width, expected.Width - tolerance, expected.Width + tolerance);
        Assert.InRange(read.Height, expected.Height - tolerance, expected.Height + tolerance);
        var (want, wantPieces) = Measured(expected);
        var (got, gotPieces) = Measured(read);
        Assert.Equal(wantPieces, gotPieces);
        for (var i = 0; i < want.Count; i++)
        {
            Assert.InRange(got[i].X, want[i].X - tolerance, want[i].X + tolerance);
            Assert.InRange(got[i].Y, want[i].Y - tolerance, want[i].Y + tolerance);
        }
    }

    private static void AssertPoints(HShape shape, params (int X, int Y)[] expected)
    {
        var (points, _) = Measured(shape);
        Assert.Equal(expected.Length, points.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(points[i].X, expected[i].X - 2, expected[i].X + 2);
            Assert.InRange(points[i].Y, expected[i].Y - 2, expected[i].Y + 2);
        }
    }

    // ───────────────────────── Round trips ─────────────────────────

    [Fact]
    public void Docx_writes_connectors_as_custom_geometry_and_reads_them_back()
    {
        var path = Path.Combine(fx.NewFolder(), "connectors.docx");
        var shapes = Connectors();
        DocxWriter.Write(Model(shapes), path);

        DocxAssert.ValidPackage(path);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var geometries = DocxAssert.Body(path).Descendants(a + "custGeom").ToList();
        Assert.Equal(2, geometries.Count);
        Assert.Equal(3, geometries[0].Descendants(a + "lnTo").Count());
        Assert.Equal(2, geometries[1].Descendants(a + "cubicBezTo").Count());
        var read = Shapes(DocxAssert.ReadBack(path));
        Assert.Equal(2, read.Count);
        AssertPath(shapes[0], read[0]);
        AssertPath(shapes[1], read[1]);
    }

    [Fact]
    public void Rtf_writes_connectors_as_free_forms_and_reads_them_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "connectors.rtf");
        var shapes = Connectors();
        RtfWriter.Write(Model(shapes), path, TestContext.Current.CancellationToken);

        var rtf = File.ReadAllText(path);
        Assert.Contains("{\\sn pSegmentInfo}{\\sv 2;3;16384;3;32768}", rtf, StringComparison.Ordinal);
        Assert.Contains("{\\sn pSegmentInfo}{\\sv 2;3;16384;8194;32768}", rtf, StringComparison.Ordinal);
        var read = Shapes(RtfReader.Read(File.ReadAllBytes(path), Path.Combine(folder, "media"), DocumentLimits.Default));
        Assert.Equal(2, read.Count);
        AssertPath(shapes[0], read[0]);
        AssertPath(shapes[1], read[1]);
    }

    [Fact]
    public void Odt_writes_connectors_as_paths_and_reads_them_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "connectors.odt");
        var shapes = Connectors();
        OdtWriter.Write(Model(shapes), path, TestContext.Current.CancellationToken);

        using (var zip = ZipFile.OpenRead(path))
        {
            using var stream = zip.GetEntry("content.xml")!.Open();
            var paths = XDocument.Load(stream).Descendants(Filee.Engines.Hwp.Hwpx.Odt.Odf.Draw + "path").ToList();
            Assert.Equal(2, paths.Count);
            Assert.All(paths, p => Assert.NotNull(p.Attribute(Filee.Engines.Hwp.Hwpx.Odt.Odf.Svg + "viewBox")));
        }
        var read = Shapes(OdtReader.Read(path, Path.Combine(folder, "media")));
        Assert.Equal(2, read.Count);
        AssertPath(shapes[0], read[0]);
        AssertPath(shapes[1], read[1]);
        // Where it is: the box of its points.
        Assert.InRange(read[0].Anchor!.HorizontalOffset, OnPaper.HorizontalOffset - 20, OnPaper.HorizontalOffset + 20);
    }

    [Fact]
    public void Hwpx_and_hwp_write_connectors_as_connect_lines_and_read_them_back()
    {
        var folder = fx.NewFolder();
        var hwpx = Path.Combine(folder, "connectors.hwpx");
        var shapes = Connectors();
        HwpxWriter.Write(Model([.. shapes, Open(Mixed, 20000, 8000, new HArrow(HArrowKind.Spear), null, offset: 20000)]), hwpx,
            cancellationToken: TestContext.Current.CancellationToken);

        using (var zip = ZipFile.OpenRead(hwpx))
        {
            using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("type=\"STROKE_ONEWAY\"", xml, StringComparison.Ordinal);
            Assert.Contains("type=\"ARC_BOTH\"", xml, StringComparison.Ordinal);
        }
        void Check(List<HShape> read)
        {
            Assert.Equal(3, read.Count);
            AssertPath(shapes[0], read[0]);
            AssertPath(shapes[1], read[1]);
            // The straight piece and curve: a bent connector through the curve's points.
            Assert.Equal(HShapeKind.Path, read[2].Kind);
            Assert.Equal(new HArrow(HArrowKind.Spear), read[2].Head);
            var (points, pieces) = Measured(read[2]);
            Assert.Equal(new string('L', 1 + OpenPaths.FlattenSteps), pieces);
            Assert.Equal((20000, 0), ((int)points[^1].X, (int)points[^1].Y));
        }
        Check(Shapes(HwpxReader.Read(hwpx, Path.Combine(folder, "a"))));
        var hwp = Path.ChangeExtension(hwpx, ".hwp");
        HwpxToHwp.Write(hwpx, hwp);
        Check(Shapes(HwpReader.Read(hwp, Path.Combine(folder, "b"))));
    }

    // ───────────────────────── Files from the applications ─────────────────────────

    [Fact]
    public void Docx_reader_reads_word_connector_presets_vml_connectors_and_open_free_forms()
    {
        const string Horizontal = "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>";
        const string Vertical = "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>";
        static string Shape(string geometry, string flip = "", string ends = "") =>
            $"<wps:wsp><wps:cNvCnPr/><wps:spPr><a:xfrm{flip}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm>{geometry}" +
            $"<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill>{ends}</a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";
        var bent = Shape("<a:prstGeom prst=\"bentConnector3\"><a:avLst><a:gd name=\"adj1\" fmla=\"val 25000\"/></a:avLst></a:prstGeom>", " flipH=\"1\"",
            "<a:headEnd type=\"oval\"/><a:tailEnd type=\"triangle\" w=\"lg\"/>");
        var curved = Shape("<a:prstGeom prst=\"curvedConnector3\"><a:avLst/></a:prstGeom>");
        var elbow = Shape("<a:prstGeom prst=\"bentConnector2\"><a:avLst/></a:prstGeom>", " flipV=\"1\"");
        var free = Shape("<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"l\" t=\"t\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"100\" h=\"100\" fill=\"none\">" +
                         "<a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"50\" y=\"100\"/></a:lnTo><a:lnTo><a:pt x=\"100\" y=\"0\"/></a:lnTo></a:path></a:pathLst></a:custGeom>");
        // Closed round a single line: no inside, left out.
        var closed = Shape("<a:custGeom><a:pathLst><a:path w=\"100\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"100\" y=\"0\"/></a:lnTo><a:close/></a:path></a:pathLst></a:custGeom>");
        var builder = new DocxBuilder();
        builder.Paragraph(P(
            string.Concat(new[] { bent, curved, elbow, free, closed }.Select(g => Anchor(ShapeUri, g, 1828800, 914400, Horizontal, Vertical))) +
            // VML: Word's elbow connector (type 34) with its adjust value, mirrored.
            "<w:r><w:pict><v:shape type=\"#_x0000_t34\" adj=\"5400\" style=\"position:absolute;margin-left:10pt;margin-top:10pt;width:144pt;height:72pt;flip:x\" o:connectortype=\"elbow\" " +
            "xmlns:o=\"urn:schemas-microsoft-com:office:office\"><v:stroke endarrow=\"block\"/></v:shape></w:pict></w:r>" + R("text")));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "word.docx"));

        var shapes = Shapes(DocxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")));

        // The line closed on itself is left out; the others are open paths in a 14400 × 7200 box.
        Assert.Equal(5, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(HShapeKind.Path, s.Kind));
        Assert.All(shapes, s => Assert.Null(s.Transform));
        Assert.All(shapes, s => Assert.False(s.Geometry!.Path!.Closed));
        // The presets are kept with their adjust values and flips; the free-form has none.
        var preset = shapes[0].Geometry!.Connector!;
        Assert.Equal(("bentConnector3", true, false), (preset.Preset, preset.FlipHorizontal, preset.FlipVertical));
        Assert.Equal([new HAdjustValue("adj1", 25000)], preset.Adjust);
        Assert.Null(shapes[3].Geometry!.Connector);
        Assert.Equal(("bentConnector3", true, 25000L), (shapes[4].Geometry!.Connector!.Preset, shapes[4].Geometry!.Connector!.FlipHorizontal, shapes[4].Geometry!.Connector!.Adjust[0].Value));
        // bentConnector3 at a quarter, mirrored: from the top right, the bend a quarter in from it.
        AssertPoints(shapes[0], (14400, 0), (10800, 0), (10800, 7200), (0, 7200));
        Assert.Equal(new HArrow(HArrowKind.Oval), shapes[0].Head);
        Assert.Equal(new HArrow(HArrowKind.Triangle, HArrowSize.Large), shapes[0].Tail);
        // curvedConnector3: two cubics through the middle.
        AssertPoints(shapes[1], (0, 0), (3600, 0), (7200, 1800), (7200, 3600), (7200, 5400), (10800, 7200), (14400, 7200));
        Assert.Equal("CC", Measured(shapes[1]).Pieces);
        // bentConnector2 mirrored upside down: from the bottom left along the bottom, then up.
        AssertPoints(shapes[2], (0, 7200), (14400, 7200), (14400, 0));
        AssertPoints(shapes[3], (0, 0), (7200, 7200), (14400, 0));
        AssertPoints(shapes[4], (14400, 0), (10800, 0), (10800, 7200), (0, 7200));
        Assert.Equal(new HArrow(HArrowKind.Triangle), shapes[4].Tail);
    }

    [Fact]
    public void Odt_reader_reads_connector_paths_types_and_open_paths()
    {
        var builder = new OdtBuilder
        {
            Styles = "<draw:marker draw:name=\"Arrow\" svg:viewBox=\"0 0 20 30\" svg:d=\"M10 0l-10 30h20z\"/>",
            ContentStyles = "<style:style style:name=\"gr1\" style:family=\"graphic\"><style:graphic-properties draw:stroke=\"solid\" svg:stroke-width=\"0cm\" " +
                            "draw:marker-end=\"Arrow\" draw:marker-end-width=\"0.3cm\"/></style:style>",
            Body = "<text:p>" +
                   // LibreOffice's own: svg:d in 1/100 mm where the connector is.
                   "<draw:connector draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"1cm\" svg:x2=\"5cm\" svg:y2=\"3cm\" svg:d=\"M1000 1000h2000v2000h2000\"/>" +
                   // Without a path: a curve, and the standard right angles.
                   "<draw:connector draw:style-name=\"gr1\" draw:type=\"curve\" text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"4cm\" svg:x2=\"5cm\" svg:y2=\"6cm\"/>" +
                   "<draw:connector draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x1=\"5cm\" svg:y1=\"7cm\" svg:x2=\"1cm\" svg:y2=\"9cm\"/>" +
                   "<draw:polyline draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x=\"1cm\" svg:y=\"10cm\" svg:width=\"4cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 4000 2000\" draw:points=\"0,0 2000,2000 4000,0\"/>" +
                   "<draw:path draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x=\"1cm\" svg:y=\"13cm\" svg:width=\"4cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 4000 2000\" svg:d=\"M0 2000 C1000 0 3000 0 4000 2000\"/>" +
                   // Closed: a filled shape without markers.
                   "<draw:path draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x=\"1cm\" svg:y=\"16cm\" svg:width=\"4cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 4000 2000\" svg:d=\"M0 0 L4000 0 L4000 2000 Z\"/>" +
                   "text</text:p>",
        };
        var path = builder.Save(Path.Combine(fx.NewFolder(), "connectors.odt"));

        var shapes = Shapes(OdtReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")));

        Assert.Equal(6, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(HShapeKind.Path, s.Kind));
        Assert.All(shapes[..5], s => Assert.Equal(HArrowKind.Triangle, s.Tail?.Kind));
        Assert.True(shapes[5].Geometry!.Path!.Closed);
        Assert.Null(shapes[5].Tail);
        const int Cm = 2835; // 1 cm in model units, rounded
        AssertNear(shapes[0], (0, 0), (2 * Cm, 0), (2 * Cm, 2 * Cm), (4 * Cm, 2 * Cm));
        Assert.InRange(shapes[0].Anchor!.HorizontalOffset, Cm - 2, Cm + 2);
        Assert.Equal("C", Measured(shapes[1]).Pieces);
        AssertNear(shapes[1], (0, 0), (2 * Cm, 0), (2 * Cm, 2 * Cm), (4 * Cm, 2 * Cm));
        // From the right to the left: the start stays at the right.
        AssertNear(shapes[2], (4 * Cm, 0), (2 * Cm, 0), (2 * Cm, 2 * Cm), (0, 2 * Cm));
        AssertNear(shapes[3], (0, 0), (2 * Cm, 2 * Cm), (4 * Cm, 0));
        Assert.InRange(shapes[3].Anchor!.VerticalOffset, 10 * Cm - 4, 10 * Cm + 4);
        AssertNear(shapes[4], (0, 2 * Cm), (Cm, 0), (3 * Cm, 0), (4 * Cm, 2 * Cm));
        AssertNear(shapes[5], (0, 0), (4 * Cm, 0), (4 * Cm, 2 * Cm));

        static void AssertNear(HShape shape, params (int X, int Y)[] expected)
        {
            var (points, _) = Measured(shape);
            Assert.Equal(expected.Length, points.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.InRange(points[i].X, expected[i].X - 5, expected[i].X + 5);
                Assert.InRange(points[i].Y, expected[i].Y - 5, expected[i].Y + 5);
            }
        }
    }

    [Fact]
    public void Hwpx_reader_reads_bent_and_curved_connect_lines_through_their_control_points()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "connectors.hwpx");
        HwpxWriter.Write(Model(new HShape(HShapeKind.Line) { Width = 20000, Height = 8000, Anchor = OnPaper }), path,
            cancellationToken: TestContext.Current.CancellationToken);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            var line = xml[xml.IndexOf("<hp:line ", StringComparison.Ordinal)..(xml.IndexOf("</hp:line>", StringComparison.Ordinal) + "</hp:line>".Length)];
            var points = line[line.IndexOf("<hc:startPt", StringComparison.Ordinal)..(line.IndexOf("/>", line.IndexOf("<hc:endPt", StringComparison.Ordinal), StringComparison.Ordinal) + 2)];
            string Connector(string type, string controls) => line
                .Replace("<hp:line ", $"<hp:connectLine type=\"{type}\" ", StringComparison.Ordinal).Replace("</hp:line>", "</hp:connectLine>", StringComparison.Ordinal)
                .Replace(" isReverseHV=\"0\"", "", StringComparison.Ordinal)
                .Replace("tailStyle=\"NORMAL\"", "tailStyle=\"ARROW\"", StringComparison.Ordinal)
                .Replace(points, "<hp:startPt x=\"0\" y=\"0\" subjectIDRef=\"0\" subjectIdx=\"0\"/><hp:endPt x=\"20000\" y=\"8000\" subjectIDRef=\"0\" subjectIdx=\"0\"/>" +
                                 $"<hp:controlPoints>{controls}</hp:controlPoints>", StringComparison.Ordinal);
            // A bent connector with its start and end among the control points, and a curved one with its two controls only.
            var bent = Connector("STROKE_ONEWAY", "<hp:point x=\"0\" y=\"0\" type=\"3\"/><hp:point x=\"10000\" y=\"0\" type=\"2\"/>" +
                                                  "<hp:point x=\"10000\" y=\"8000\" type=\"2\"/><hp:point x=\"20000\" y=\"8000\" type=\"26\"/>");
            var curved = Connector("ARC_ONEWAY", "<hp:point x=\"10000\" y=\"0\" type=\"2\"/><hp:point x=\"10000\" y=\"8000\" type=\"2\"/>");
            // The ends among the points under other type numbers: still the ends, not points between.
            var numbered = Connector("ARC_ONEWAY", "<hp:point x=\"0\" y=\"0\" type=\"1\"/><hp:point x=\"10000\" y=\"0\" type=\"2\"/>" +
                                                   "<hp:point x=\"10000\" y=\"8000\" type=\"2\"/><hp:point x=\"20000\" y=\"8000\" type=\"0\"/>");
            var changed = xml.Replace(line, bent + curved + numbered, StringComparison.Ordinal);
            Assert.NotEqual(xml, changed);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(changed);
        }

        var shapes = Shapes(HwpxReader.Read(path, Path.Combine(folder, "media")));

        Assert.Equal(3, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(new HArrow(HArrowKind.Triangle), s.Tail));
        AssertPoints(shapes[0], (0, 0), (10000, 0), (10000, 8000), (20000, 8000));
        Assert.Equal("LLL", Measured(shapes[0]).Pieces);
        AssertPoints(shapes[1], (0, 0), (10000, 0), (10000, 8000), (20000, 8000));
        Assert.Equal("C", Measured(shapes[1]).Pieces);
        AssertPoints(shapes[2], (0, 0), (10000, 0), (10000, 8000), (20000, 8000));
        Assert.Equal("C", Measured(shapes[2]).Pieces);
    }

    [Fact]
    public void Rtf_reader_reads_word_connectors_and_open_free_forms_without_the_picture_copy()
    {
        static string Property(string name, string value) => $"{{\\sp{{\\sn {name}}}{{\\sv {value}}}}}";
        static string Shape(string properties) =>
            "{\\shp{\\*\\shpinst\\shpleft0\\shptop0\\shpright2880\\shpbottom1440\\shpbxcolumn\\shpbypara\\shpwr3" + properties +
            "}{\\shprslt{\\*\\do\\dobxcolumn\\dobypara\\dpline COPY}}}";
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\pard\\plain " +
                  Shape(Property("shapeType", "34") + Property("adjustValue", "5400") + Property("fFlipH", "1") + Property("lineEndArrowhead", "1")) +
                  Shape(Property("shapeType", "38")) +
                  Shape(Property("shapeType", "0") + Property("geoRight", "100") + Property("geoBottom", "100") +
                        Property("pVerticies", "8;3;(0,0);(50,100);(100,0)") + Property("pSegmentInfo", "2;4;0x4000;0x0002;0x8000;0x8000")) +
                  // Closed: a filled shape (Word's white by default).
                  Shape(Property("shapeType", "0") + Property("geoRight", "100") + Property("geoBottom", "100") +
                        Property("pVerticies", "8;3;(0,0);(50,100);(100,0)") + Property("pSegmentInfo", "2;4;16384;2;24577;32768")) +
                  "after\\par}";

        var paragraph = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default).Sections[0].Blocks.OfType<HParagraph>().First();

        var shapes = paragraph.Inlines.OfType<HShape>().ToList();
        Assert.Equal(4, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(HShapeKind.Path, s.Kind));
        Assert.Equal(("bentConnector3", true), (shapes[0].Geometry!.Connector!.Preset, shapes[0].Geometry!.Connector!.FlipHorizontal));
        Assert.True(shapes[3].Geometry!.Path!.Closed);
        Assert.Equal("#FFFFFF", shapes[3].Fill);
        AssertPoints(shapes[3], (0, 0), (7200, 7200), (14400, 0));
        // 2880 × 1440 twips = 14400 × 7200 units; the bend a quarter in from the right after mirroring.
        AssertPoints(shapes[0], (14400, 0), (10800, 0), (10800, 7200), (0, 7200));
        Assert.Equal(new HArrow(HArrowKind.Triangle), shapes[0].Tail);
        Assert.Equal("CC", Measured(shapes[1]).Pieces);
        AssertPoints(shapes[2], (0, 0), (7200, 7200), (14400, 0));
        Assert.Equal("after", HDocumentWalker.Text(paragraph.Inlines));
    }

    [Fact]
    public void Pptx_reader_reads_bent_connectors_as_paths()
    {
        // A slide's bent connector (p:cxnSp, bentConnector3 at a quarter, mirrored) going into the document model.
        var pptx = new PptxBuilder();
        pptx.Slide("<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"5\" name=\"Elbow\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>" +
                   "<a:xfrm flipH=\"1\"><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm>" +
                   "<a:prstGeom prst=\"bentConnector3\"><a:avLst><a:gd name=\"adj1\" fmla=\"val 25000\"/></a:avLst></a:prstGeom>" +
                   "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></p:spPr></p:cxnSp>");
        var dir = fx.NewFolder();

        var read = Filee.Engines.Hwp.Hwpx.Pptx.PptxReader.Read(pptx.Save(Path.Combine(dir, "elbow.pptx")), Path.Combine(dir, "media"));

        // (The slide's background is a rectangle of its own.)
        var shapes = read.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HShape>().ToList();
        Assert.DoesNotContain(shapes, s => s.Kind == HShapeKind.Line);
        var shape = Assert.Single(shapes, s => s.Kind == HShapeKind.Path);
        AssertPoints(shape, (14400, 0), (10800, 0), (10800, 7200), (0, 7200));
    }

    [Fact]
    public void Paths_too_long_are_not_read_and_finish_quickly()
    {
        // A free-form of 50,000 lines in DOCX and RTF: not read as a path (Word draws it; the model takes 4096 pieces).
        const string Horizontal = "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>";
        const string Vertical = "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>";
        var lines = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"<a:lnTo><a:pt x=\"{i % 100}\" y=\"{i % 7}\"/></a:lnTo>"));
        var free = "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:custGeom><a:pathLst><a:path w=\"100\" h=\"100\">" +
                   $"<a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo>{lines}</a:path></a:pathLst></a:custGeom><a:ln w=\"12700\"/></wps:spPr><wps:bodyPr/></wps:wsp>";
        var builder = new DocxBuilder();
        builder.Paragraph(P(Anchor(ShapeUri, free, 914400, 914400, Horizontal, Vertical) + R("text")));
        var docx = builder.Save(Path.Combine(fx.NewFolder(), "long.docx"));
        Assert.Empty(Shapes(DocxReader.Read(docx, Path.Combine(Path.GetDirectoryName(docx)!, "media"))));

        var vertices = "8;50001;" + string.Join(";", Enumerable.Range(0, 50_001).Select(i => $"({i % 100},{i % 7})"));
        var rtf = "{\\rtf1\\ansi\\pard\\plain {\\shp{\\*\\shpinst\\shpleft0\\shptop0\\shpright2880\\shpbottom1440" +
                  $"{{\\sp{{\\sn shapeType}}{{\\sv 0}}}}{{\\sp{{\\sn pVerticies}}{{\\sv {vertices}}}}}}}}}after\\par}}";
        var paragraph = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default).Sections[0].Blocks.OfType<HParagraph>().First();
        Assert.Empty(paragraph.Inlines.OfType<HShape>());

        // An ODF connector whose path is megabytes long falls back to the connector's own route.
        var odt = new OdtBuilder
        {
            Body = "<text:p><draw:connector text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"1cm\" svg:x2=\"5cm\" svg:y2=\"3cm\" svg:d=\"M0 0" +
                   string.Concat(Enumerable.Repeat(" L1 1", 300_000)) + "\"/>text</text:p>",
        }.Save(Path.Combine(fx.NewFolder(), "long.odt"));
        var routed = Assert.Single(Shapes(OdtReader.Read(odt, Path.Combine(Path.GetDirectoryName(odt)!, "media"))));
        Assert.Equal("LLL", Measured(routed).Pieces);
    }

    // ───────────────────────── Connector presets and closed paths ─────────────────────────

    /// <summary>A triangle, closed.</summary>
    private static readonly HPath Triangle = new(Pt(0, 8000), [new(Pt(10000, 0)), new(Pt(20000, 8000))], Closed: true);

    /// <summary>Two cubics round and back to the start, closed.</summary>
    private static readonly HPath Drop = new(Pt(10000, 0),
        [new(Pt(10000, 8000), Pt(20000, 0), Pt(20000, 8000)), new(Pt(10000, 0), Pt(0, 8000), Pt(0, 0))], Closed: true);

    private static HShape[] ClosedShapes()
    {
        var shapes = new[] { Open(Triangle, 20000, 8000, null, null), Open(Drop, 20000, 8000, null, null, offset: 10000) };
        foreach (var shape in shapes)
            shape.Fill = "#C00000";
        return shapes;
    }

    private static void AssertClosed(HShape expected, HShape read, string? pieces = null)
    {
        Assert.True(read.Geometry?.Path?.Closed, "closed");
        Assert.Equal(expected.Fill, read.Fill, StringComparer.OrdinalIgnoreCase);
        Assert.Null(read.Head);
        Assert.Null(read.Tail);
        if (pieces is null)
        {
            AssertPath(expected, read);
            return;
        }
        // Curves flattened: the pieces as given, the corners on the expected outline's corners where they are kept.
        Assert.Equal(pieces, Measured(read).Pieces);
    }

    [Fact]
    public void Closed_paths_go_through_every_writer_filled_and_come_back_closed()
    {
        var folder = fx.NewFolder();
        var shapes = ClosedShapes();

        var docx = Path.Combine(folder, "closed.docx");
        DocxWriter.Write(Model(shapes), docx);
        DocxAssert.ValidPackage(docx);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        Assert.Equal(2, DocxAssert.Body(docx).Descendants(a + "close").Count());
        var fromDocx = Shapes(DocxAssert.ReadBack(docx));
        Assert.Equal(2, fromDocx.Count);
        AssertClosed(shapes[0], fromDocx[0]);
        AssertClosed(shapes[1], fromDocx[1]);

        var rtf = Path.Combine(folder, "closed.rtf");
        RtfWriter.Write(Model(shapes), rtf, TestContext.Current.CancellationToken);
        Assert.Contains("{\\sn pSegmentInfo}{\\sv 2;4;16384;2;24577;32768}", File.ReadAllText(rtf), StringComparison.Ordinal);
        var fromRtf = Shapes(RtfReader.Read(File.ReadAllBytes(rtf), Path.Combine(folder, "media"), DocumentLimits.Default));
        Assert.Equal(2, fromRtf.Count);
        AssertClosed(shapes[0], fromRtf[0]);
        AssertClosed(shapes[1], fromRtf[1]);

        var odt = Path.Combine(folder, "closed.odt");
        OdtWriter.Write(Model(shapes), odt, TestContext.Current.CancellationToken);
        var fromOdt = Shapes(OdtReader.Read(odt, Path.Combine(folder, "media2")));
        Assert.Equal(2, fromOdt.Count);
        AssertClosed(shapes[0], fromOdt[0]);
        AssertClosed(shapes[1], fromOdt[1]);

        // 한글: a polygon, the curves flattened; HWP the same through its binary.
        var hwpx = Path.Combine(folder, "closed.hwpx");
        HwpxWriter.Write(Model(shapes), hwpx, cancellationToken: TestContext.Current.CancellationToken);
        using (var zip = ZipFile.OpenRead(hwpx))
        {
            using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(reader.ReadToEnd(), "<hp:polygon "));
        }
        void Check(List<HShape> read)
        {
            Assert.Equal(2, read.Count);
            AssertClosed(shapes[0], read[0]);
            // Two cubics of 8 pieces each, the last corner the start again: 15 pieces and the close.
            AssertClosed(shapes[1], read[1], new string('L', 2 * OpenPaths.FlattenSteps - 1));
        }
        Check(Shapes(HwpxReader.Read(hwpx, Path.Combine(folder, "a"))));
        var hwp = Path.ChangeExtension(hwpx, ".hwp");
        HwpxToHwp.Write(hwpx, hwp);
        Check(Shapes(HwpReader.Read(hwp, Path.Combine(folder, "b"))));
    }

    [Fact]
    public void Word_closed_free_forms_are_filled_paths()
    {
        const string Horizontal = "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>";
        const string Vertical = "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>";
        var triangle = "<wps:wsp><wps:cNvSpPr/><wps:spPr><a:xfrm flipV=\"1\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm>" +
                       "<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"l\" t=\"t\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"100\" h=\"100\">" +
                       "<a:moveTo><a:pt x=\"0\" y=\"100\"/></a:moveTo><a:lnTo><a:pt x=\"50\" y=\"0\"/></a:lnTo><a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo><a:close/></a:path></a:pathLst></a:custGeom>" +
                       "<a:solidFill><a:srgbClr val=\"2E75B6\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";
        // Two contours (a ring): not one path, left out.
        var ring = "<wps:wsp><wps:cNvSpPr/><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm>" +
                   "<a:custGeom><a:pathLst><a:path w=\"100\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"100\" y=\"0\"/></a:lnTo><a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo><a:close/>" +
                   "<a:moveTo><a:pt x=\"25\" y=\"25\"/></a:moveTo><a:lnTo><a:pt x=\"75\" y=\"25\"/></a:lnTo><a:lnTo><a:pt x=\"75\" y=\"75\"/></a:lnTo><a:close/></a:path></a:pathLst></a:custGeom>" +
                   "<a:solidFill><a:srgbClr val=\"2E75B6\"/></a:solidFill></wps:spPr><wps:bodyPr/></wps:wsp>";
        var builder = new DocxBuilder();
        builder.Paragraph(P(Anchor(ShapeUri, triangle, 1828800, 914400, Horizontal, Vertical) + Anchor(ShapeUri, ring, 914400, 914400, Horizontal, Vertical) + R("text")));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "closed.docx"));

        var shape = Assert.Single(Shapes(DocxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"))));

        Assert.Equal(HShapeKind.Path, shape.Kind);
        Assert.True(shape.Geometry!.Path!.Closed);
        Assert.Equal("#2E75B6", shape.Fill, StringComparer.OrdinalIgnoreCase);
        // A closed outline has no ends: the arrowhead is dropped. Upside down: the apex at the bottom.
        Assert.Null(shape.Tail);
        AssertPoints(shape, (0, 0), (7200, 7200), (14400, 0));
    }

    [Fact]
    public void Connector_presets_from_docx_and_pptx_are_written_back_to_docx_as_presets()
    {
        const string Horizontal = "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>";
        const string Vertical = "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>";
        static string Shape(string geometry, string flip = "", string ends = "") =>
            $"<wps:wsp><wps:cNvCnPr/><wps:spPr><a:xfrm{flip}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm>{geometry}" +
            $"<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill>{ends}</a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";
        var bent = Shape("<a:prstGeom prst=\"bentConnector3\"><a:avLst><a:gd name=\"adj1\" fmla=\"val 25000\"/></a:avLst></a:prstGeom>", " flipH=\"1\"",
            "<a:headEnd type=\"oval\"/><a:tailEnd type=\"triangle\" w=\"lg\"/>");
        var curved = Shape("<a:prstGeom prst=\"curvedConnector4\"><a:avLst><a:gd name=\"adj1\" fmla=\"val 30000\"/><a:gd name=\"adj2\" fmla=\"val 70000\"/></a:avLst></a:prstGeom>",
            " flipV=\"1\"", "<a:tailEnd type=\"stealth\"/>");
        var builder = new DocxBuilder();
        builder.Paragraph(P(
            Anchor(ShapeUri, bent, 1828800, 914400, Horizontal, Vertical) + Anchor(ShapeUri, curved, 1828800, 914400, Horizontal, Vertical) +
            "<w:r><w:pict><v:shape type=\"#_x0000_t34\" adj=\"5400\" style=\"position:absolute;margin-left:10pt;margin-top:10pt;width:144pt;height:72pt;flip:x\" o:connectortype=\"elbow\" " +
            "xmlns:o=\"urn:schemas-microsoft-com:office:office\"><v:stroke endarrow=\"block\"/></v:shape></w:pict></w:r>" + R("text")));
        var folder = fx.NewFolder();
        var model = DocxReader.Read(builder.Save(Path.Combine(folder, "word.docx")), Path.Combine(folder, "media"));
        var first = Shapes(model);
        Assert.Equal(3, first.Count);

        var again = Path.Combine(folder, "again.docx");
        DocxWriter.Write(model, again);

        DocxAssert.ValidPackage(again);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        var body = DocxAssert.Body(again);
        Assert.Empty(body.Descendants(a + "custGeom"));
        Assert.Equal(3, body.Descendants(wps + "cNvCnPr").Count());
        var presets = body.Descendants(a + "prstGeom").Select(p =>
            $"{(string?)p.Attribute("prst")} {string.Join(",", p.Descendants(a + "gd").Select(g => $"{(string?)g.Attribute("name")}={(string?)g.Attribute("fmla")}"))}").ToList();
        // The VML connector's adj 5400 of 21600: a quarter, as DrawingML counts it.
        Assert.Equal(["bentConnector3 adj1=val 25000", "curvedConnector4 adj1=val 30000,adj2=val 70000", "bentConnector3 adj1=val 25000"], presets);
        var flips = body.Descendants(wps + "spPr").Select(p => p.Element(a + "xfrm")!).Select(x => ((string?)x.Attribute("flipH"), (string?)x.Attribute("flipV"))).ToList();
        Assert.Equal([("1", null), (null, "1"), ("1", null)], flips);
        var read = Shapes(DocxAssert.ReadBack(again));
        Assert.Equal(3, read.Count);
        for (var i = 0; i < 3; i++)
        {
            AssertPath(first[i], read[i]);
            Assert.Equal(first[i].Geometry!.Connector!.Preset, read[i].Geometry!.Connector!.Preset);
        }

        // A slide's connectors (p:cxnSp, and p:sp with a connector preset) and line, with their arrowheads.
        var pptx = new PptxBuilder();
        pptx.Slide(
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"5\" name=\"Elbow\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>" +
            "<a:xfrm flipH=\"1\"><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm>" +
            "<a:prstGeom prst=\"bentConnector3\"><a:avLst><a:gd name=\"adj1\" fmla=\"val 25000\"/></a:avLst></a:prstGeom>" +
            "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:headEnd type=\"diamond\" w=\"sm\" len=\"sm\"/><a:tailEnd type=\"triangle\" w=\"lg\" len=\"lg\"/></a:ln></p:spPr></p:cxnSp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"6\" name=\"Curve\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>" +
            "<a:xfrm><a:off x=\"914400\" y=\"2743200\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"curvedConnector3\"><a:avLst/></a:prstGeom>" +
            "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:tailEnd type=\"arrow\"/></a:ln></p:spPr></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"7\" name=\"Line\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>" +
            "<a:xfrm><a:off x=\"914400\" y=\"4572000\"/><a:ext cx=\"1828800\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>" +
            "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:headEnd type=\"oval\"/><a:tailEnd type=\"stealth\"/></a:ln></p:spPr></p:sp>");
        var slides = Filee.Engines.Hwp.Hwpx.Pptx.PptxReader.Read(pptx.Save(Path.Combine(folder, "deck.pptx")), Path.Combine(folder, "media3"));
        var drawn = slides.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HShape>()
            .Where(s => s.Kind is HShapeKind.Path or HShapeKind.Line).ToList();
        Assert.Equal(3, drawn.Count);
        Assert.Equal((new HArrow(HArrowKind.Diamond, HArrowSize.Small, HArrowSize.Small), new HArrow(HArrowKind.Triangle, HArrowSize.Large, HArrowSize.Large)),
            (drawn[0].Head, drawn[0].Tail));
        Assert.Equal(("curvedConnector3", null, new HArrow(HArrowKind.Open)), (drawn[1].Geometry?.Connector?.Preset, drawn[1].Head, drawn[1].Tail));
        Assert.Equal((HShapeKind.Line, new HArrow(HArrowKind.Oval), new HArrow(HArrowKind.Stealth)), (drawn[2].Kind, drawn[2].Head, drawn[2].Tail));

        var fromSlides = Path.Combine(folder, "slides.docx");
        DocxWriter.Write(slides, fromSlides);
        DocxAssert.ValidPackage(fromSlides);
        var slideBody = DocxAssert.Body(fromSlides);
        Assert.Equal(["bentConnector3", "curvedConnector3"], slideBody.Descendants(a + "prstGeom").Select(p => (string?)p.Attribute("prst"))
            .Where(p => p is not null && p.Contains("Connector", StringComparison.Ordinal)).ToList());
        Assert.Equal(3, slideBody.Descendants(a + "tailEnd").Count());
    }

    [Fact]
    public void The_slide_reader_draws_odf_connectors_along_their_route()
    {
        // A drawing with three connectors 8 × 4 cm: along its svg:d (in 1/100 mm where it is), the standard route
        // without one (right angles turning halfway), and a curve.
        var builder = new OdpBuilder
        {
            Drawing = true,
            ContentStyles = OdpBuilder.GraphicStyle("c", "draw:stroke=\"solid\" svg:stroke-color=\"#000000\" svg:stroke-width=\"0.1cm\" draw:fill=\"none\""),
            Pages = OdpBuilder.Page(
                "<draw:connector draw:style-name=\"c\" svg:x1=\"1cm\" svg:y1=\"1cm\" svg:x2=\"9cm\" svg:y2=\"5cm\" svg:d=\"M1000 1000h2000v4000h6000\"/>" +
                "<draw:connector draw:style-name=\"c\" svg:x1=\"1cm\" svg:y1=\"7cm\" svg:x2=\"9cm\" svg:y2=\"11cm\"/>" +
                "<draw:connector draw:style-name=\"c\" draw:type=\"curve\" svg:x1=\"1cm\" svg:y1=\"13cm\" svg:x2=\"9cm\" svg:y2=\"17cm\"/>" +
                "<draw:connector draw:style-name=\"c\" draw:type=\"line\" svg:x1=\"1cm\" svg:y1=\"19cm\" svg:x2=\"9cm\" svg:y2=\"23cm\"/>"),
        };
        var path = builder.Save(Path.Combine(fx.NewFolder(), "connectors.odg"));

        var deck = OdfDrawReader.Read(path, cancellationToken: TestContext.Current.CancellationToken);

        var shapes = Assert.Single(deck.Slides).Elements.Cast<SlideShape>().ToList();
        Assert.Equal(4, shapes.Count);
        const float Cm = 72 / 2.54f;
        Assert.All(shapes, s => Assert.InRange(s.Bounds.Width, 8 * Cm - 0.5f, 8 * Cm + 0.5f));
        Assert.Equal(4, shapes[0].Paths[0].Path.PointCount);
        Assert.Equal(4, shapes[1].Paths[0].Path.PointCount);
        Assert.True(shapes[2].Paths[0].Path.SegmentMasks.HasFlag(SKPathSegmentMask.Cubic));
        Assert.Equal(2, shapes[3].Paths[0].Path.PointCount);

        using var picture = SlideOutput.ToBitmaps(deck, 72, [0], TestContext.Current.CancellationToken).Single();
        bool Dark(float x, float y) => picture.GetPixel((int)Math.Round(x * Cm), (int)Math.Round(y * Cm)).Red < 128;
        // svg:d: along the top to 3 cm, down, along the bottom; not the straight line from end to end.
        Assert.True(Dark(2, 1) && Dark(3, 3) && Dark(6, 5));
        Assert.False(Dark(5, 3));
        // Standard: the bend halfway, at 5 cm.
        Assert.True(Dark(3, 7) && Dark(5, 9) && Dark(7, 11));
        Assert.False(Dark(3, 8));
        // The straight one still is.
        Assert.True(Dark(5, 21));
    }

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public void The_renderer_draws_a_bent_connector_through_its_corners_with_its_arrowhead()
    {
        // A bent connector 200 × 80 pt from (100, 100): along the top to x 150, down to y 180, along to x 300, an arrow
        // at the end; 1 pt wide.
        var shape = Open(Bent, 20000, 8000, null, new HArrow(HArrowKind.Triangle));
        shape.Line = new HBorder(HBorderStyle.Solid, 25.4 / 72, "#000000");
        const int Scale = 4;
        using var page = SKBitmap.Decode(FileeRenderer.ToImages(Model(shape), 72 * Scale, cancellationToken: TestContext.Current.CancellationToken).First());
        bool Dark(float x, float y) => page.GetPixel((int)(x * Scale), (int)(y * Scale)).Red < 128;

        Assert.True(Dark(125, 100));
        Assert.True(Dark(150, 140));
        Assert.True(Dark(225, 180));
        // Not the straight line from corner to corner.
        Assert.False(Dark(200, 140));
        // The arrowhead at the end (6 pt across: 3 × 2 pt), 1 pt from its back.
        Assert.True(Dark(295, 180 - 2.2f) && Dark(295, 180 + 2.2f));
    }
}
