// Filee's slide renderer (Slides/): PPTX read into the slide model and drawn as PDF pages and pictures, without
// LibreOffice. Every deck is written in code (SlideDeckBuilder, RenderQuality/SlideFeatures).

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Slides;
using Filee.Engines.Tests.RenderQuality;
using SkiaSharp;
using static Filee.Engines.Tests.SlideDeckBuilder;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public sealed class SlideRendererTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-slides-{Guid.NewGuid():N}");

    public SlideRendererTests() => Directory.CreateDirectory(_folder);

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

    private string Save(SlideDeckBuilder deck, string name = "deck.pptx") => deck.Save(Path.Combine(_folder, name));

    private static SlideDeck Read(string path) => PptxSlideReader.Read(path, cancellationToken: Token);

    // ───────────────────────── Decks ─────────────────────────

    [Fact]
    public void The_feature_deck_is_drawn_as_a_pdf_and_pictures()
    {
        var path = SlideFeatures.Write(Path.Combine(_folder, "features.pptx"));
        var deck = Read(path);
        Assert.Equal(SlideFeatures.Names.Length, deck.Slides.Count);
        Assert.Equal(960, deck.Width, 0.01);
        Assert.Equal(540, deck.Height, 0.01);

        var pdf = SlideOutput.ToPdf(deck, cancellationToken: Token);
        using (var document = PigDocument.Open(pdf))
        {
            Assert.Equal(SlideFeatures.Names.Length, document.NumberOfPages);
            Assert.All(document.GetPages(), page => Assert.Equal(960, page.Width, 0.5));
        }
        var pictures = SlideOutput.ToBitmaps(deck, 48, [0, 3], Token).ToList();
        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, p => Assert.Equal(640, p.Width));
        foreach (var picture in pictures)
            picture.Dispose();
    }

    [Fact]
    public void Placeholders_take_position_style_and_bullets_from_the_layout_and_master()
    {
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.Placeholder("title", Paragraph("Heading")) + deck.Placeholder("body", Paragraph("Point") + Paragraph("Deeper", "<a:pPr lvl=\"1\"/>"), 1), Layout.Content);
        var slide = Assert.Single(Read(Save(deck)).Slides);

        var shapes = slide.Elements.OfType<SlideShape>().Where(s => s.Text is not null).ToList();
        var title = shapes.Single(s => Text(s).Contains("Heading"));
        // The layout's title has no box of its own: the master's.
        Assert.Equal(new SKRect(66, 28.75f, 894, 133.15f), title.Bounds, new RectComparer());
        var heading = title.Text!.Paragraphs[0].Inlines.OfType<HText>().Single();
        Assert.Equal(4400, heading.Format.Size);
        Assert.Equal("Calibri Light", heading.Format.Font);
        Assert.Equal(TextAnchor.Middle, title.Text.Anchor);

        var body = shapes.Single(s => Text(s).Contains("Point"));
        var first = body.Text!.Paragraphs[0];
        Assert.Equal("•", ((HText)first.Inlines[0]).Text);
        Assert.IsType<HTab>(first.Inlines[1]);
        Assert.Equal(1800, first.Format.Left);
        Assert.Equal(-1800, first.Format.FirstLine);
        Assert.Equal(2800, first.Inlines.OfType<HText>().Last().Format.Size);
        Assert.Equal(new HLineHeight(HLineHeightRule.Multiple, 216), first.Format.Layout!.LineHeight);
        Assert.Equal(2400, body.Text.Paragraphs[1].Inlines.OfType<HText>().Last().Format.Size);
        // Master decorations: the bar under the slide's own shapes; master placeholders are not drawn.
        Assert.Equal(new SolidFill(new SKColor(0xED, 0x7D, 0x31)), slide.Elements.OfType<SlideShape>().First().Fill);
        Assert.DoesNotContain(slide.Elements.OfType<SlideShape>(), s => Text(s) is "title" or "body" or "sldNum");
    }

    [Fact]
    public void Theme_styles_fills_lines_and_text_colours_are_resolved()
    {
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.Shape("roundRect", 10, 10, 100, 50, paragraphs: Paragraph("Styled")) +
                   deck.Shape("rect", 200, 10, 100, 50, "<a:solidFill><a:schemeClr val=\"accent2\"><a:lumMod val=\"50000\"/><a:alpha val=\"40000\"/></a:schemeClr></a:solidFill>",
                       "<a:ln w=\"38100\" cap=\"rnd\" cmpd=\"dbl\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:prstDash val=\"dash\"/><a:bevel/><a:tailEnd type=\"stealth\" w=\"lg\" len=\"sm\"/></a:ln>", styled: false));
        var shapes = Read(Save(deck)).Slides[0].Elements.OfType<SlideShape>().Skip(1).ToList();

        // fillRef idx 1 = the theme's first fill in accent 1; lnRef idx 2 = 1 pt in accent 1 shaded; fontRef = lt1 text.
        Assert.Equal(new SolidFill(new SKColor(0x44, 0x72, 0xC4)), shapes[0].Fill);
        Assert.Equal(1, shapes[0].Line!.Width, 0.01);
        Assert.True(((SolidFill)shapes[0].Line!.Fill).Color.Red < 0x44);
        Assert.Equal("#FFFFFF", shapes[0].Text!.Paragraphs[0].Inlines.OfType<HText>().Single().Format.Color);

        var fill = Assert.IsType<SolidFill>(shapes[1].Fill).Color;
        Assert.Equal(new SKColor(0x84, 0x3C, 0x0B, 102), fill); // Office shows 843C0C for "Orange, Accent 2, Darker 50%"
        var line = shapes[1].Line!;
        Assert.Equal((3f, SKStrokeCap.Round, SKStrokeJoin.Bevel, LineCompound.Double), (line.Width, line.Cap, line.Join, line.Compound));
        Assert.Equal([4f, 3f], line.Dash);
        Assert.Equal(new LineEnd(LineEndKind.Stealth, 5, 2), line.Tail);
        Assert.Equal(LineEnd.None, line.Head);
    }

    [Fact]
    public void Tables_take_the_default_style_and_merged_cells()
    {
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.Table(10, 10, [100, 100, 100], 30,
            [Cell("A") + Cell("B") + Cell("C"), Cell("Wide", attributes: " gridSpan=\"2\"") + Cell("", attributes: " hMerge=\"1\"") + Cell("1")]));
        var table = Assert.IsType<SlideTable>(Assert.Single(Read(Save(deck)).Slides[0].Elements.Skip(1)));

        Assert.Equal([100f, 100f, 100f], table.Columns);
        var header = table.Rows[0].Cells[0];
        Assert.Equal(new SolidFill(new SKColor(0x44, 0x72, 0xC4)), header.Fill);
        var run = header.Text.Paragraphs[0].Inlines.OfType<HText>().Single();
        Assert.True(run.Format.Bold);
        Assert.Equal("#FFFFFF", run.Format.Color);
        Assert.Equal(3, header.Bottom!.Width, 0.01);
        Assert.Equal(2, table.Rows[1].Cells[0].ColumnSpan);
        Assert.True(table.Rows[1].Cells[1].Covered);
        // A body row: a tint of accent 1.
        Assert.NotEqual(header.Fill, table.Rows[1].Cells[0].Fill);
    }

    [Fact]
    public void Hidden_slides_are_left_out_and_smartart_and_charts_draw_what_they_store()
    {
        var path = SlideFeatures.Write(Path.Combine(_folder, "features.pptx"));
        var last = Read(path).Slides[^1];
        var smartArt = Assert.IsType<SlideGroup>(last.Elements[1]);
        Assert.Equal(3, smartArt.Children.Count);
        Assert.Equal(["Plan", "Build", "Ship"], smartArt.Children.OfType<SlideShape>().Select(Text));
        // The chart's fallback picture.
        Assert.IsType<PictureFill>(Assert.IsType<SlideShape>(last.Elements[2]).Fill);

        var deck = new SlideDeckBuilder();
        deck.Slide(deck.TextBox(0, 0, 100, 40, Paragraph("shown"))).Slide(deck.TextBox(0, 0, 100, 40, Paragraph("hidden")), hidden: true);
        Assert.Single(Read(Save(deck, "hidden.pptx")).Slides);
    }

    [Fact]
    public void Groups_map_their_child_space_and_vertical_text_turns()
    {
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.Group(100, 100, 200, 100, 0.5, deck.Shape("rect", 0, 0, 400, 200, Solid("#00FF00"), styled: false)) +
                   deck.Shape("rect", 400, 50, 60, 300, Solid("#FFFFFF"), "", Paragraph("Vertical", "", "<a:rPr lang=\"en-US\" sz=\"2400\"/>"),
                       "<a:bodyPr vert=\"vert\" anchor=\"ctr\"/>", styled: false));
        var deckRead = Read(Save(deck));
        using var shaper = new Typesetting.Shaper();
        using var renderer = new SlideRenderer(new Typesetting.FontResolver(), shaper, Token);
        var page = renderer.Render(deckRead, deckRead.Slides[0]);
        using var bitmap = page.Render(72);
        // The group's child (400 × 200 in child space) fills the group's box at half the size.
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(110, 110));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(290, 190));
        Assert.NotEqual(SKColors.Lime, bitmap.GetPixel(310, 210));
        // Vertical text: its glyphs are turned (one transformed run inside the tall box).
        var run = page.Items.OfType<Rendering.GlyphItem>().Single(g => g.Run.Text.Contains("Vertical")).Run;
        Assert.NotNull(run.Transform);
        Assert.Equal(1, Math.Abs(run.Transform!.Value.SkewX), 0.01);
    }

    private static string Text(SlideShape shape) =>
        string.Concat(shape.Text?.Paragraphs.SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text) ?? []);

    private sealed class RectComparer : IEqualityComparer<SKRect>
    {
        public bool Equals(SKRect a, SKRect b) =>
            Math.Abs(a.Left - b.Left) < 0.05 && Math.Abs(a.Top - b.Top) < 0.05 && Math.Abs(a.Right - b.Right) < 0.05 && Math.Abs(a.Bottom - b.Bottom) < 0.05;

        public int GetHashCode(SKRect rect) => 0;
    }

    // ───────────────────────── Geometry and colours ─────────────────────────

    [Fact]
    public void Every_preset_draws_inside_its_box()
    {
        foreach (var name in PresetShapes.Names)
        {
            var result = ShapeGeometry.Evaluate(PresetShapes.Find(name)!, 1_270_000, 762_000, null, 1 / 12700.0);
            Assert.NotEmpty(result.Paths);
            foreach (var path in result.Paths)
            {
                var bounds = path.Path.TightBounds;
                Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= 100.5 && bounds.Bottom <= 60.5 || name.StartsWith("wedge", StringComparison.Ordinal),
                    $"{name}: {bounds}");
                Assert.True(path.Path.PointCount > 1, name);
            }
            Assert.True(result.TextRect is { } text && text.Width >= 0 && text.Height >= 0, name);
        }
    }

    [Fact]
    public void Arcs_follow_the_stretched_ellipse()
    {
        var ellipse = ShapeGeometry.Evaluate(PresetShapes.Find("ellipse")!, 200, 100, null, 1);
        var bounds = ellipse.Paths[0].Path.TightBounds;
        Assert.Equal(new SKRect(0, 0, 200, 100), bounds, new RectComparer());
        Assert.True(ellipse.Paths[0].Path.Contains(100, 50));
        Assert.False(ellipse.Paths[0].Path.Contains(5, 5));

        // A pie from 0° to 90° (clockwise, y down): the lower right quarter.
        var pie = ShapeGeometry.Evaluate(PresetShapes.Find("pie")!, 200, 100, new Dictionary<string, string> { ["adj1"] = "val 0", ["adj2"] = "val 5400000" }, 1);
        Assert.True(pie.Paths[0].Path.Contains(150, 70));
        Assert.False(pie.Paths[0].Path.Contains(50, 30));
    }

    [Theory]
    [InlineData("*/ 10 3 2", 15)]
    [InlineData("+- 10 3 2", 11)]
    [InlineData("+/ 10 2 3", 4)]
    [InlineData("?: -1 5 7", 7)]
    [InlineData("abs -4", 4)]
    [InlineData("at2 0 10", 5_400_000)]
    [InlineData("cos 10 5400000", 0)]
    [InlineData("sin 10 5400000", 10)]
    [InlineData("max 3 9", 9)]
    [InlineData("mod 3 4 0", 5)]
    [InlineData("pin 0 120 100", 100)]
    [InlineData("sqrt 16", 4)]
    [InlineData("val w", 200)]
    [InlineData("*/ 1 2 0", 0)]
    [InlineData("nonsense 1 2", 0)]
    public void Guide_formulas_compute_as_drawingml_defines_them(string formula, double expected)
    {
        var values = new Dictionary<string, double> { ["w"] = 200 };
        Assert.Equal(expected, ShapeGeometry.Formula(formula, values), 6);
    }

    [Fact]
    public void Colour_transforms_apply_in_order()
    {
        var colors = DrawingColors.FromTheme(null);
        static XElement Fill(string xml) => XElement.Parse($"<a:solidFill xmlns:a=\"{DrawingColors.A.NamespaceName}\">{xml}</a:solidFill>");
        Assert.Equal(new SKColor(0x44, 0x72, 0xC4), colors.Color(Fill("<a:schemeClr val=\"accent1\"/>")));
        // tx1 is dk1 under the default map, and the map can swap it.
        Assert.Equal(SKColors.Black, colors.Color(Fill("<a:schemeClr val=\"tx1\"/>")));
        var swapped = colors.WithMap(XElement.Parse("<map tx1=\"lt1\"/>"));
        Assert.Equal(SKColors.White, swapped.Color(Fill("<a:schemeClr val=\"tx1\"/>")));
        Assert.Equal(new SKColor(0x80, 0x80, 0x80, 0x80), colors.Color(Fill("<a:srgbClr val=\"FFFFFF\"><a:lumMod val=\"50000\"/><a:alpha val=\"50000\"/></a:srgbClr>")));
        Assert.Equal(new SKColor(0x80, 0x80, 0x80), colors.Color(Fill("<a:prstClr val=\"black\"><a:lumOff val=\"50000\"/></a:prstClr>")));
        Assert.Equal(new SKColor(0x00, 0xFF, 0xFF), colors.Color(Fill("<a:srgbClr val=\"FF0000\"><a:comp/></a:srgbClr>")));
        Assert.Equal(new SKColor(9, 9, 9), colors.Color(Fill("<a:srgbClr val=\"FF0000\"><a:shade val=\"0\"/><a:redOff val=\"3500\"/><a:greenOff val=\"3500\"/><a:blueOff val=\"3500\"/></a:srgbClr>")));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_file_that_is_no_deck_is_invalid_data()
    {
        var text = Path.Combine(_folder, "text.pptx");
        File.WriteAllText(text, "not a zip");
        Assert.Throws<InvalidDataException>(() => Read(text));

        var empty = Path.Combine(_folder, "empty.pptx");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            zip.CreateEntry("hello.txt");
        Assert.Throws<InvalidDataException>(() => Read(empty));

        var broken = Path.Combine(_folder, "broken.pptx");
        new SlideDeckBuilder().Slide("<p:sp><unclosed>").Save(broken);
        Assert.Throws<InvalidDataException>(() => Read(broken));
        Assert.Null(SlideCapabilities.FirstUnsupported(text));
    }

    [Fact]
    public void Groups_nested_without_end_are_refused()
    {
        var deck = new SlideDeckBuilder();
        var nested = deck.Shape("rect", 0, 0, 10, 10, Solid("#000000"), styled: false);
        for (var i = 0; i < 60; i++)
            nested = deck.Group(0, 0, 10, 10, 1, nested);
        deck.Slide(nested);
        var refusal = Assert.Throws<ConversionRefusedException>(() => Read(Save(deck)));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
    }

    [Fact]
    public void Damaged_numbers_sizes_and_pictures_stay_contained()
    {
        var deck = new SlideDeckBuilder();
        // A picture whose header claims 100,000 × 100,000 pixels is left out before anything decodes it.
        var huge = TestPicture(4, 4);
        huge[16] = 0;
        huge[17] = 1;
        huge[18] = 0x86;
        huge[19] = 0xA0;
        huge[20] = 0;
        huge[21] = 1;
        huge[22] = 0x86;
        huge[23] = 0xA0;
        var media = deck.Media(huge);
        const string wild =
            "<a:custGeom><a:avLst/><a:gdLst><a:gd name=\"x\" fmla=\"*/ 1e300 1e300 1\"/><a:gd name=\"y\" fmla=\"+/ x x 0\"/></a:gdLst><a:rect l=\"x\" t=\"y\" r=\"NaN\" b=\"b\"/>" +
            "<a:pathLst><a:path w=\"0\" h=\"-5\"><a:moveTo><a:pt x=\"x\" y=\"y\"/></a:moveTo><a:arcTo wR=\"x\" hR=\"1\" stAng=\"NaN\" swAng=\"99999999999\"/>" +
            "<a:lnTo><a:pt x=\"1e40\" y=\"-1e40\"/></a:lnTo><a:cubicBezTo><a:pt x=\"1\" y=\"1\"/></a:cubicBezTo><a:close/></a:path></a:pathLst></a:custGeom>";
        deck.Slide(deck.Shape(wild, -1e9, 1e12, 1e13, -5, Solid("#FF0000"), "<a:ln w=\"999999999999\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:custDash><a:ds d=\"0\" sp=\"0\"/></a:custDash><a:tailEnd type=\"triangle\"/></a:ln>",
                       Paragraph("text"), "<a:bodyPr numCol=\"99999\" lIns=\"-1\" rot=\"NaN\"/>", styled: false, rotation: double.MaxValue) +
                   deck.Picture("rIdBig", 0, 0, 100, 100), pictures: new Dictionary<string, string> { ["rIdBig"] = media });
        var read = Read(Save(deck));
        Assert.DoesNotContain(read.Slides[0].Elements.OfType<SlideShape>(), s => s.IsPicture);
        var pdf = SlideOutput.ToPdf(read, cancellationToken: Token);
        Assert.True(pdf.Length > 0);
    }
}
