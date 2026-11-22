// Arrowheads on lines in the document model (HShape.Head / Tail): a model built in code goes through the DOCX, RTF,
// ODT, HWPX and HWP writers and back; Word-like DOCX (DrawingML headEnd / tailEnd, VML v:stroke on v:line and on a
// straight connector), RTF (lineStartArrowhead / lineEndArrowhead, no picture copy), ODT (draw:marker told by its
// outline) and HWPX (hp:lineShape headStyle / tailStyle, a straight hp:connectLine) built in code are read into it;
// the renderer draws the heads at the lines' ends.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class LineArrowTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly HAnchor OnPaper = new("PAPER", "LEFT", 10000, "PAPER", "TOP", 10000, "IN_FRONT_OF_TEXT", true);

    private static HShape Line(HArrow? head, HArrow? tail, bool flip = false, int offset = 0) => new(HShapeKind.Line)
    {
        Width = 20000,
        Height = 5000,
        Anchor = OnPaper with { VerticalOffset = OnPaper.VerticalOffset + offset },
        Line = new HBorder(HBorderStyle.Solid, 0.5, "#C00000"),
        FlipHorizontal = flip,
        Head = head,
        Tail = tail,
    };

    /// <summary>Lines with the arrowheads every writer has: a plain arrow, both ends, a mirrored line.</summary>
    private static HDocument Model(params HShape[] lines)
    {
        var document = new HDocument();
        var section = new HSection();
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(lines);
        paragraph.Inlines.Add(new HText("arrows", default));
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);
        return document;
    }

    private static readonly HShape[] Common =
    [
        Line(null, new HArrow(HArrowKind.Triangle)),
        Line(new HArrow(HArrowKind.Oval, HArrowSize.Small, HArrowSize.Large), new HArrow(HArrowKind.Open, HArrowSize.Large, HArrowSize.Small), offset: 6000),
        Line(new HArrow(HArrowKind.Stealth, HArrowSize.Large, HArrowSize.Large), new HArrow(HArrowKind.Diamond, HArrowSize.Small, HArrowSize.Small), flip: true, offset: 12000),
    ];

    private static List<HShape> Shapes(HDocument document) =>
        [.. document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HShape>()];

    private static void AssertArrows(IReadOnlyList<HShape> expected, IReadOnlyList<HShape> read)
    {
        Assert.Equal(expected.Count, read.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(HShapeKind.Line, read[i].Kind);
            Assert.Equal((expected[i].Head, expected[i].Tail), (read[i].Head, read[i].Tail));
            Assert.Equal((expected[i].FlipHorizontal, expected[i].FlipVertical), (read[i].FlipHorizontal, read[i].FlipVertical));
        }
    }

    // ───────────────────────── Round trips ─────────────────────────

    [Fact]
    public void Docx_writes_arrowheads_as_head_and_tail_ends_and_reads_them_back()
    {
        var path = Path.Combine(fx.NewFolder(), "arrows.docx");
        DocxWriter.Write(Model(Common), path);

        DocxAssert.ValidPackage(path);
        var ends = DocxAssert.Body(path).Descendants(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/main") + "tailEnd").Select(e => ((string?)e.Attribute("type"), (string?)e.Attribute("w"), (string?)e.Attribute("len")));
        Assert.Equal([("triangle", "med", "med"), ("arrow", "lg", "sm"), ("diamond", "sm", "sm")], ends);
        AssertArrows(Common, Shapes(DocxAssert.ReadBack(path)));
    }

    [Fact]
    public void Rtf_writes_arrowheads_as_shape_properties_and_reads_them_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "arrows.rtf");
        RtfWriter.Write(Model(Common), path, TestContext.Current.CancellationToken);

        var rtf = File.ReadAllText(path);
        Assert.Contains("{\\sn lineEndArrowhead}{\\sv 1}", rtf, StringComparison.Ordinal);
        Assert.Contains("{\\sn lineStartArrowWidth}{\\sv 0}", rtf, StringComparison.Ordinal);
        AssertArrows(Common, Shapes(RtfReader.Read(File.ReadAllBytes(path), Path.Combine(folder, "media"), DocumentLimits.Default)));
    }

    [Fact]
    public void Odt_writes_arrowheads_as_markers_and_reads_them_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "arrows.odt");
        HShape[] lines = [.. Common, Line(new HArrow(HArrowKind.Square), null, offset: 18000)];
        OdtWriter.Write(Model(lines), path, TestContext.Current.CancellationToken);

        using (var zip = ZipFile.OpenRead(path))
        {
            using var stream = zip.GetEntry("styles.xml")!.Open();
            var markers = XDocument.Load(stream).Descendants(Filee.Engines.Hwp.Hwpx.Odt.Odf.Draw + "marker").ToList();
            Assert.Equal(6, markers.Count);
            // Style names have no spaces.
            Assert.All(markers, m => Assert.DoesNotContain(' ', (string)m.Attribute(Filee.Engines.Hwp.Hwpx.Odt.Odf.Draw + "name")!));
        }
        AssertArrows(lines, Shapes(OdtReader.Read(path, Path.Combine(folder, "media"))));
    }

    [Fact]
    public void Hwpx_and_hwp_write_arrowheads_in_the_line_shape_and_read_them_back()
    {
        var folder = fx.NewFolder();
        var hwpx = Path.Combine(folder, "arrows.hwpx");
        HShape[] lines =
        [
            Line(new HArrow(HArrowKind.Spear), new HArrow(HArrowKind.Triangle, HArrowSize.Small, HArrowSize.Large)),
            Line(new HArrow(HArrowKind.Oval, Hollow: true), new HArrow(HArrowKind.Square, HArrowSize.Large, HArrowSize.Medium), flip: true, offset: 6000),
            Line(new HArrow(HArrowKind.Stealth), new HArrow(HArrowKind.Diamond, Hollow: true), offset: 12000),
        ];
        HwpxWriter.Write(Model(lines), hwpx, cancellationToken: TestContext.Current.CancellationToken);

        AssertArrows(lines, Shapes(HwpxReader.Read(hwpx, Path.Combine(folder, "a"))));
        var hwp = Path.ChangeExtension(hwpx, ".hwp");
        HwpxToHwp.Write(hwpx, hwp);
        AssertArrows(lines, Shapes(HwpReader.Read(hwp, Path.Combine(folder, "b"))));
    }

    // ───────────────────────── Word-like files ─────────────────────────

    [Fact]
    public void Docx_reader_reads_drawingml_and_vml_arrowheads()
    {
        const string Horizontal = "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>";
        const string Vertical = "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>";
        const string Connector =
            "<wps:wsp><wps:cNvCnPr/><wps:spPr><a:xfrm flipH=\"1\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
            "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:headEnd type=\"stealth\" w=\"sm\" len=\"lg\"/><a:tailEnd type=\"triangle\"/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";
        var builder = new DocxBuilder();
        builder.Paragraph(P(
            Anchor(ShapeUri, Connector, 1828800, 914400, Horizontal, Vertical) +
            // VML: a line drawn from right to left with an arrow at its end, and Word's straight arrow connector (type 32), mirrored.
            "<w:r><w:pict><v:line from=\"144pt,0\" to=\"0,72pt\" strokeweight=\"1pt\"><v:stroke endarrow=\"block\" endarrowwidth=\"wide\" endarrowlength=\"short\"/></v:line>" +
            "<v:shapetype xmlns:o=\"urn:schemas-microsoft-com:office:office\" id=\"_x0000_t32\" coordsize=\"21600,21600\" o:spt=\"32\" path=\"m,l21600,21600e\" filled=\"f\"/>" +
            "<v:shape type=\"#_x0000_t32\" style=\"position:absolute;margin-left:10pt;margin-top:10pt;width:100pt;height:50pt;flip:y\"><v:stroke startarrow=\"oval\" startarrowwidth=\"narrow\" endarrow=\"open\"/></v:shape>" +
            "</w:pict></w:r>" + R("text")));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "word.docx"));

        var shapes = Shapes(DocxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")));

        Assert.Equal(3, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(HShapeKind.Line, s.Kind));
        // The connector: mirrored by its flips only (not mirrored once more by its transform).
        Assert.Equal((true, false), (shapes[0].FlipHorizontal, shapes[0].FlipVertical));
        Assert.Null(shapes[0].Transform);
        Assert.Equal(new HArrow(HArrowKind.Stealth, HArrowSize.Small, HArrowSize.Large), shapes[0].Head);
        Assert.Equal(new HArrow(HArrowKind.Triangle), shapes[0].Tail);
        // v:line from the top right to the bottom left.
        Assert.Equal((true, false), (shapes[1].FlipHorizontal, shapes[1].FlipVertical));
        Assert.Null(shapes[1].Head);
        Assert.Equal(new HArrow(HArrowKind.Triangle, HArrowSize.Large, HArrowSize.Small), shapes[1].Tail);
        Assert.Equal((false, true), (shapes[2].FlipHorizontal, shapes[2].FlipVertical));
        Assert.Null(shapes[2].Transform);
        Assert.Equal((10000, 5000), (shapes[2].Width, shapes[2].Height));
        Assert.Equal(new HArrow(HArrowKind.Oval, HArrowSize.Small, HArrowSize.Medium), shapes[2].Head);
        Assert.Equal(new HArrow(HArrowKind.Open), shapes[2].Tail);
    }

    [Fact]
    public void Rtf_reader_reads_word_arrowheads_without_the_picture_copy()
    {
        static string Property(string name, string value) => $"{{\\sp{{\\sn {name}}}{{\\sv {value}}}}}";
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\pard\\plain " +
                  "{\\shp{\\*\\shpinst\\shpleft0\\shptop0\\shpright2880\\shpbottom1440\\shpbxcolumn\\shpbypara\\shpwr3" +
                  Property("shapeType", "32") + Property("fFlipH", "1") + Property("lineStartArrowhead", "4") + Property("lineStartArrowWidth", "2") +
                  Property("lineEndArrowhead", "2") + Property("lineEndArrowLength", "0") +
                  "}{\\shprslt{\\*\\do\\dobxcolumn\\dobypara\\dpline COPY}}}" +
                  "after\\par}";

        var paragraph = RtfReader.Read(Encoding.ASCII.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default).Sections[0].Blocks.OfType<HParagraph>().First();

        var line = Assert.Single(paragraph.Inlines.OfType<HShape>());
        Assert.True(line.FlipHorizontal);
        Assert.Equal(new HArrow(HArrowKind.Oval, HArrowSize.Large, HArrowSize.Medium), line.Head);
        Assert.Equal(new HArrow(HArrowKind.Stealth, HArrowSize.Medium, HArrowSize.Small), line.Tail);
        Assert.Equal("after", HDocumentWalker.Text(paragraph.Inlines));
    }

    [Theory]
    // Outlines drawn the way LibreOffice draws its markers (relative paths; the circle as a polygon, the notched arrow with a curved-in back).
    [InlineData("M10 0l-10 30h20z", "Triangle")]
    [InlineData("M0 564l564 567 567-567-567-564z", "Diamond")]
    [InlineData("M0 0h10v10h-10z", "Square")]
    [InlineData("M462 1118l-102-29-102-51-93-72-72-93-51-102-29-102-13-105 13-102 29-106 51-102 72-89 93-72 102-50 102-34 106-9 101 9 106 34 98 50 93 72 72 89 51 102 29 106 13 102-13 105-29 102-51 102-72 93-93 72-98 51-106 29-101 13z", "Oval")]
    [InlineData("m1013 1491 118 89-567-1580-564 1580 114-85 136-68 148-46 161-17 161 13 153 46z", "Stealth")]
    // A chevron drawn as a thin outline, and a circle of curves.
    [InlineData("M10 0 L20 27 L17 30 L10 11 L3 30 L0 27 Z", "Open")]
    [InlineData("M10 0 C15.5 0 20 4.5 20 10 C20 15.5 15.5 20 10 20 C4.5 20 0 15.5 0 10 C0 4.5 4.5 0 10 0 Z", "Oval")]
    public void Odt_markers_are_told_apart_by_their_outline(string path, string kind) =>
        Assert.Equal(Enum.Parse<HArrowKind>(kind), OdfMarkers.Kind(OdfMarkers.Points(path)));

    [Fact]
    public void A_marker_path_is_read_only_so_far()
    {
        // Megabytes of path on a marker: read up to its first 64 K characters / 4000 numbers.
        var points = OdfMarkers.Points("M0 0" + string.Concat(Enumerable.Repeat("L1 1 L2 0 ", 200_000)) + "Z");
        Assert.InRange(points.Count, 1, 2001);
        Assert.Empty(OdfMarkers.Points(new string('Q', 3_000_000)));
    }

    [Fact]
    public void Odt_reader_reads_line_markers_and_straight_connectors()
    {
        var builder = new OdtBuilder
        {
            Styles = "<draw:marker draw:name=\"Arrow\" svg:viewBox=\"0 0 20 30\" svg:d=\"M10 0l-10 30h20z\"/>" +
                     "<draw:marker draw:name=\"Circle\" svg:viewBox=\"0 0 20 20\" svg:d=\"M10 0 C15.5 0 20 4.5 20 10 C20 15.5 15.5 20 10 20 C4.5 20 0 15.5 0 10 C0 4.5 4.5 0 10 0 Z\"/>",
            ContentStyles = "<style:style style:name=\"gr1\" style:family=\"graphic\"><style:graphic-properties draw:stroke=\"solid\" svg:stroke-width=\"0cm\" " +
                            "draw:marker-start=\"Circle\" draw:marker-start-width=\"0.15cm\" draw:marker-end=\"Arrow\" draw:marker-end-width=\"0.3cm\"/></style:style>" +
                            "<style:style style:name=\"gr2\" style:family=\"graphic\"><style:graphic-properties draw:stroke=\"none\" draw:marker-end=\"Arrow\"/></style:style>",
            Body = "<text:p><draw:line draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x1=\"4cm\" svg:y1=\"1cm\" svg:x2=\"1cm\" svg:y2=\"2cm\"/>" +
                   "<draw:connector draw:style-name=\"gr1\" draw:type=\"line\" text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"3cm\" svg:x2=\"4cm\" svg:y2=\"3cm\"/>" +
                   "<draw:connector draw:style-name=\"gr1\" text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"4cm\" svg:x2=\"4cm\" svg:y2=\"5cm\"/>" +
                   "<draw:line draw:style-name=\"gr2\" text:anchor-type=\"paragraph\" svg:x1=\"1cm\" svg:y1=\"6cm\" svg:x2=\"4cm\" svg:y2=\"6cm\"/>text</text:p>",
        };
        var path = builder.Save(Path.Combine(fx.NewFolder(), "lines.odt"));

        var shapes = Shapes(OdtReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")));

        // The bent connector is an open path (ConnectorTests); a line without an outline has no arrowheads.
        Assert.Equal(4, shapes.Count);
        Assert.Equal(HShapeKind.Path, shapes[2].Kind);
        Assert.Equal(shapes[0].Tail, shapes[2].Tail);
        shapes.RemoveAt(2);
        Assert.True(shapes[0].FlipHorizontal);
        // On a hairline (counted as 2 pt): a 0.15 cm circle is about 2 line widths, a 0.3 cm arrow about 4 wide and 6 long.
        Assert.Equal(new HArrow(HArrowKind.Oval, HArrowSize.Small, HArrowSize.Small), shapes[0].Head);
        Assert.Equal(new HArrow(HArrowKind.Triangle, HArrowSize.Large, HArrowSize.Large), shapes[0].Tail);
        Assert.Equal(shapes[0].Tail, shapes[1].Tail);
        Assert.Null(shapes[2].Tail);
    }

    [Fact]
    public void Hwpx_reader_reads_hollow_marks_sizes_and_straight_connectors()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "lines.hwpx");
        HwpxWriter.Write(Model(Line(null, null)), path, cancellationToken: TestContext.Current.CancellationToken);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            var line = xml[xml.IndexOf("<hp:line ", StringComparison.Ordinal)..(xml.IndexOf("</hp:line>", StringComparison.Ordinal) + "</hp:line>".Length)];
            var marked = line.Replace("headStyle=\"NORMAL\"", "headStyle=\"EMPTY_CIRCLE\"", StringComparison.Ordinal)
                .Replace("headSz=\"MEDIUM_MEDIUM\"", "headSz=\"SMALL_LARGE\"", StringComparison.Ordinal)
                .Replace("tailStyle=\"NORMAL\"", "tailStyle=\"CONCAVE_ARROW\"", StringComparison.Ordinal);
            // The same line as a straight connector (연결선) and as a bent one without control points, drawn straight too.
            var connector = marked.Replace("<hp:line ", "<hp:connectLine type=\"STRAIGHT_ONEWAY\" ", StringComparison.Ordinal).Replace("</hp:line>", "</hp:connectLine>", StringComparison.Ordinal);
            var bent = connector.Replace("STRAIGHT_ONEWAY", "STROKE_ONEWAY", StringComparison.Ordinal);
            var changed = xml.Replace(line, marked + connector + bent, StringComparison.Ordinal);
            Assert.NotEqual(xml, changed);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(changed);
        }

        var shapes = Shapes(HwpxReader.Read(path, Path.Combine(folder, "media")));

        Assert.Equal(3, shapes.Count);
        Assert.All(shapes, s =>
        {
            Assert.Equal(HShapeKind.Line, s.Kind);
            Assert.Equal(new HArrow(HArrowKind.Oval, HArrowSize.Small, HArrowSize.Large, Hollow: true), s.Head);
            Assert.Equal(new HArrow(HArrowKind.Stealth), s.Tail);
        });
    }

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public void The_renderer_draws_arrowheads_at_the_ends_of_lines()
    {
        // Two flat lines 1 pt wide from x 100 to 300 pt: one plain, one with an arrow at its end and a hollow circle at
        // its start (both 6 pt across on a line thinner than 2 pt: 3 × 2 pt).
        HShape Flat(HArrow? head, HArrow? tail, int top) => new(HShapeKind.Line)
        {
            Width = 20000,
            Height = 0,
            Anchor = new HAnchor("PAPER", "LEFT", 10000, "PAPER", "TOP", top, "IN_FRONT_OF_TEXT", true),
            Line = new HBorder(HBorderStyle.Solid, 25.4 / 72, "#000000"),
            Head = head,
            Tail = tail,
        };
        var document = Model(Flat(null, null, 10000), Flat(new HArrow(HArrowKind.Oval, Hollow: true), new HArrow(HArrowKind.Triangle), 20000));
        const int Scale = 4;
        using var page = SKBitmap.Decode(FileeRenderer.ToImages(document, 72 * Scale, cancellationToken: TestContext.Current.CancellationToken).First());
        bool Dark(float x, float y) => page.GetPixel((int)(x * Scale), (int)(y * Scale)).Red < 128;

        // 1 pt from the arrow's back (at 294 pt), 2.2 pt off the line: inside the arrow (2.5 pt across there), not the line.
        Assert.True(Dark(295, 200 - 2.2f) && Dark(295, 200 + 2.2f));
        Assert.False(Dark(295, 100 - 2.2f) || Dark(295, 100 + 2.2f));
        // The hollow circle at the start: its outline dark, its middle (the line's end, which stops short of it) not.
        Assert.True(Dark(100 - 3, 200));
        Assert.False(Dark(100, 200));
        Assert.True(Dark(150, 100));
        Assert.True(Dark(200, 200));
    }
}
