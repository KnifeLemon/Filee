// Text wrap around floating objects in the layout engine (backlog P1-3; issues #556, #1313): lines laid out anew go on
// both sides of an object, its left or right side only or its wider side, keep the object's distance from the text,
// go below objects text goes above and below and below objects that leave no stretch wide enough, and follow a tight
// wrap's polygon. The readers bring Word's wrapText, wrap polygon and distances (DrawingML and VML) and 한글's textFlow
// and outMargin. Documents are built in code and laid out with the Noto fonts of build/fetch-fonts.ps1
// (RepositoryFonts); without them the layout tests skip.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class FloatingWrapTests : IDisposable
{
    /// <summary>300 × 400 pt paper, 10 pt margins and header and footer areas: a column from x 10 to 290, from y 20.</summary>
    private static readonly HPage Wide = new(30000, 40000, 1000, 1000, 1000, 1000, 1000, 1000, 0);

    /// <summary>Where a 10 pt line's baseline is below its top (한글's 160% lines put it 0.85 × the size down).</summary>
    private const float Ascent = 8.5f;

    /// <summary>Forty words: more than the rows beside a 40 pt high object hold.</summary>
    private static readonly string Words = string.Join(' ', Enumerable.Repeat("word", 40));

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-wrap").FullName;

    public void Dispose()
    {
        _shaper.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────────────────────── Stretches ─────────────────────────

    [Fact]
    public void The_stretches_beside_objects_follow_each_objects_sides_and_distance()
    {
        // Column 0–300; objects 100–160 and 220–260 across, 0–40 down.
        static PlacedObject Placed(float left, float right, string flow, int distance = 0) =>
            FloatingObjects.Placed(Box(100, 100, Floating("SQUARE", flow), distance), Floating("SQUARE", flow), new SKRect(left, 0, right, 40), 0);

        List<(float, float)>? Stretches(params PlacedObject[] objects) => FloatingObjects.Beside(objects, 0, 300, 10, 16, out _);

        Assert.Equal([(0f, 100f), (160f, 300f)], Stretches(Placed(100, 160, "BOTH_SIDES")));
        Assert.Equal([(0f, 95f), (165f, 300f)], Stretches(Placed(100, 160, "BOTH_SIDES", distance: 500)));
        Assert.Equal([(0f, 100f)], Stretches(Placed(100, 160, "LEFT_ONLY")));
        Assert.Equal([(160f, 300f)], Stretches(Placed(100, 160, "RIGHT_ONLY")));
        // The wider side: 140 on the right against 100 on the left.
        Assert.Equal([(160f, 300f)], Stretches(Placed(100, 160, "LARGEST_ONLY")));
        Assert.Equal([(0f, 100f), (160f, 220f), (260f, 300f)], Stretches(Placed(100, 160, "BOTH_SIDES"), Placed(220, 260, "BOTH_SIDES")));
        // A line below the objects (from 50) has none beside it; neither has one beside an object in front of the text.
        Assert.Null(FloatingObjects.Beside([Placed(100, 160, "BOTH_SIDES")], 0, 300, 50, 16, out _));
        var front = FloatingObjects.Placed(Box(100, 100, Floating("IN_FRONT_OF_TEXT")), Floating("IN_FRONT_OF_TEXT"), new SKRect(100, 0, 160, 40), 0);
        Assert.Null(FloatingObjects.Beside([front], 0, 300, 10, 16, out _));
    }

    [Fact]
    public void A_wrap_polygon_narrows_a_line_to_the_outlines_width_at_its_height()
    {
        // A triangle over the left half of a 100 × 100 object: as wide as the object at its top, nothing at its bottom.
        var anchor = Floating("SQUARE") with { WrapPolygon = [(0, 0), (1, 0), (0, 1)] };
        var placed = FloatingObjects.Placed(Box(100, 100, anchor), anchor, new SKRect(0, 0, 100, 100), 0);

        Assert.Equal((0f, 100f), placed.Across(0, 10)!.Value);
        Assert.Equal((0f, 50f), placed.Across(50, 60)!.Value);
        Assert.Equal((0f, 10f), placed.Across(90, 100)!.Value);
        Assert.Null(placed.Across(100, 110));
    }

    // ───────────────────────── Composed pages ─────────────────────────

    [Fact]
    public void Square_both_sides_puts_a_row_of_text_left_and_right_of_an_object_in_the_middle_of_the_column()
    {
        // A 60 × 40 pt object in the middle of the column (120–180), 5 pt from the text on either side: rows of text
        // from the column's edge to 115 and from 185 to the edge while beside it, the whole column below it.
        var paragraph = Paragraph(Words);
        paragraph.Inlines.Insert(0, Box(6000, 4000, Floating("SQUARE", align: "CENTER"), distance: 500));

        var page = Compose(paragraph)[0];

        var rows = Rows(page);
        var beside = rows.Where(r => r.Baseline < 60 + Ascent).ToList();
        Assert.Equal(3, beside.Count);
        foreach (var row in beside)
        {
            var left = row.Runs.Where(r => r.Positions[0].X < 115).ToList();
            var right = row.Runs.Where(r => r.Positions[0].X >= 115).ToList();
            Assert.NotEmpty(left);
            Assert.NotEmpty(right);
            Assert.Equal(10f, left.Min(r => r.Positions[0].X), 0.5f);
            Assert.All(left, r => Assert.All(r.Positions, p => Assert.True(p.X < 115, $"{p.X} in the left part")));
            Assert.Equal(185f, right.Min(r => r.Positions[0].X), 0.5f);
        }
        Assert.All(rows.Except(beside), r => Assert.Equal(10f, r.Runs.Min(run => run.Positions[0].X), 0.5f));
        Assert.Equal(Normalise(Words), Normalise(string.Concat(Runs(page).OrderBy(r => r.Positions[0].Y).ThenBy(r => r.Positions[0].X).Select(r => r.Text))));
        // No ink between the text and the object: the 5 pt kept free on each side.
        using var pixels = Pixels(page);
        for (var y = 21; y < 59; y++)
        {
            for (var x = 116; x < 120; x++)
                Assert.False(Dark(pixels, x, y), $"ink at ({x}, {y})");
            for (var x = 181; x < 185; x++)
                Assert.False(Dark(pixels, x, y), $"ink at ({x}, {y})");
        }
    }

    [Theory]
    [InlineData("LEFT_ONLY", "CENTER", 0, true, false)]
    [InlineData("RIGHT_ONLY", "CENTER", 0, false, true)]
    // 40 pt from the column's edge (50–110): 35 pt of room on the left, 175 on the right; the wider side only.
    [InlineData("LARGEST_ONLY", "LEFT", 4000, false, true)]
    [InlineData("BOTH_SIDES", "LEFT", 4000, true, true)]
    public void Text_goes_on_the_sides_of_an_object_its_flow_lets_it(string flow, string align, int offset, bool left, bool right)
    {
        var paragraph = Paragraph(Words);
        var anchor = Floating("SQUARE", flow, align, offset);
        paragraph.Inlines.Insert(0, Box(6000, 4000, anchor, distance: 500));

        var page = Compose(paragraph)[0];

        var area = FloatingObjects.Place(anchor, 60, 40, new AnchorFrame(default, default, new SKRect(10, 10, 290, 390), new SKRect(10, 20, 290, 380), true));
        foreach (var row in Rows(page).Where(r => r.Baseline < area.Bottom + Ascent))
        {
            Assert.Equal(left, row.Runs.Any(r => r.Positions[0].X < area.Left));
            Assert.Equal(right, row.Runs.Any(r => r.Positions[0].X >= area.Right - 0.5f));
        }
    }

    [Fact]
    public void An_object_at_the_column_edge_leaves_no_text_in_a_gap_too_narrow_for_it()
    {
        // 10 pt from the column's right edge (220–280), 5 pt from the text: 5 pt left over on the right stays empty.
        var paragraph = Paragraph(Words);
        paragraph.Inlines.Insert(0, Box(6000, 4000, Floating("SQUARE", align: "RIGHT", offset: 1000), distance: 500));

        var page = Compose(paragraph)[0];

        var beside = Rows(page).Where(r => r.Baseline < 60 + Ascent).ToList();
        Assert.Equal(3, beside.Count);
        Assert.All(beside, row => Assert.All(row.Runs, r => Assert.All(r.Positions, p => Assert.True(p.X < 215, $"{p.X} beside the object"))));
        Assert.All(beside, row => Assert.Equal(10f, row.Runs.Min(r => r.Positions[0].X), 0.5f));
    }

    [Fact]
    public void A_line_goes_below_an_object_that_leaves_no_stretch_wide_enough_beside_it()
    {
        // 270 of the column's 280 pt: no line beside it, the first one under it (its bottom at 60).
        var paragraph = Paragraph(Words);
        paragraph.Inlines.Insert(0, Box(27000, 4000, Floating("SQUARE", align: "CENTER")));

        var page = Compose(paragraph)[0];

        var first = Rows(page)[0];
        Assert.Equal(60 + Ascent, first.Baseline, 0.05f);
        Assert.Equal(10f, first.Runs.Min(r => r.Positions[0].X), 0.5f);
    }

    [Fact]
    public void Top_and_bottom_keeps_the_objects_distance_below_it()
    {
        float FirstBaseline(int distance)
        {
            var paragraph = Paragraph("below");
            paragraph.Inlines.Insert(0, Box(6000, 2000, Floating("TOP_AND_BOTTOM", align: "CENTER"), distance, bottom: true));
            return Rows(Compose(paragraph)[0])[0].Baseline;
        }

        Assert.Equal(10f, FirstBaseline(1000) - FirstBaseline(0), 0.05f);
    }

    [Fact]
    public void A_tight_wrap_follows_its_polygon_down_the_object()
    {
        // A triangle over the left half of a 100 × 100 object at the column's left edge: the lines beside it start
        // where its slanted side is at their top, further left the lower they are.
        var paragraph = Paragraph(Words);
        paragraph.Inlines.Insert(0, Box(10000, 10000, Floating("SQUARE") with { WrapPolygon = [(0, 0), (1, 0), (0, 1)] }));

        var page = Compose(paragraph)[0];

        var starts = Rows(page).Where(r => r.Baseline < 120).Select(r => (Top: r.Baseline - Ascent, Start: r.Runs.Min(run => run.Positions[0].X))).ToList();
        Assert.True(starts.Count >= 5, $"{starts.Count} rows beside the object");
        foreach (var (top, start) in starts)
            Assert.Equal(10 + 100 * (1 - (top - 20) / 100), start, 1f);
    }

    [Fact]
    public void The_lines_of_a_row_stay_on_one_page_with_widow_control_too()
    {
        // Rows of two lines beside an object taller than a short page (a body of five rows), for paragraphs of many
        // lengths: no row is split between pages (one with its right part and not its left), and rows don't overlap.
        var small = new HPage(30000, 12000, 1000, 1000, 1000, 1000, 1000, 1000, 0);
        for (var count = 36; count <= 60; count += 2)
        {
            var text = string.Join(' ', Enumerable.Repeat("word", count));
            var paragraph = Paragraph(text);
            paragraph.Format = paragraph.Format with { WidowOrphan = true };
            paragraph.Inlines.Insert(0, Box(6000, 30000, Floating("SQUARE", align: "CENTER"), distance: 500));

            var pages = Compose(paragraph, small);

            Assert.Equal(Normalise(text), Normalise(string.Concat(pages.SelectMany(p => Runs(p).OrderBy(r => r.Positions[0].Y).ThenBy(r => r.Positions[0].X)).Select(r => r.Text))));
            foreach (var rows in pages.Select(Rows))
            {
                Assert.All(rows, r => Assert.True(!r.Runs.Any(run => run.Positions[0].X > 180) || r.Runs.Any(run => run.Positions[0].X < 115),
                    $"{count} words: a row at {r.Baseline} has its right part only"));
                for (var i = 1; i < rows.Count; i++)
                    Assert.True(rows[i].Baseline - rows[i - 1].Baseline >= 16 - 0.05f, $"{count} words: {rows[i - 1].Baseline} then {rows[i].Baseline}");
            }
        }
    }

    // ───────────────────────── Word documents ─────────────────────────

    [Fact]
    public void Word_drawingml_anchors_bring_their_wrap_side_polygon_and_distance_from_the_text()
    {
        var square = Anchor(ShapeUri, Rectangle, 1080000, 720000, Column, Paragraph0,
            "<wp:wrapSquare wrapText=\"largest\" distT=\"36000\" distB=\"72000\" distL=\"108000\" distR=\"180000\"/>");
        var tight = Anchor(ShapeUri, Rectangle, 1080000, 720000, Column, Paragraph0,
            "<wp:wrapTight wrapText=\"left\" distL=\"0\" distR=\"0\"><wp:wrapPolygon edited=\"0\"><wp:start x=\"0\" y=\"0\"/><wp:lineTo x=\"21600\" y=\"0\"/>" +
            "<wp:lineTo x=\"0\" y=\"21600\"/><wp:lineTo x=\"0\" y=\"0\"/></wp:wrapPolygon></wp:wrapTight>");

        var shapes = Objects(Read(new DocxBuilder().Paragraph(P(square + tight)))).OfType<HShape>().ToList();

        Assert.Equal(("SQUARE", "LARGEST_ONLY", null), (shapes[0].Anchor!.Wrap, shapes[0].Anchor!.TextFlow, shapes[0].Anchor!.WrapPolygon));
        Assert.Equal(new HInsets(HwpxUnits.FromEmu(108000), HwpxUnits.FromEmu(180000), HwpxUnits.FromEmu(36000), HwpxUnits.FromEmu(72000)), shapes[0].OuterMargin);
        Assert.Equal(("SQUARE", "LEFT_ONLY"), (shapes[1].Anchor!.Wrap, shapes[1].Anchor!.TextFlow));
        Assert.Equal([(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (0.0, 0.0)], shapes[1].Anchor!.WrapPolygon!);
    }

    [Fact]
    public void A_word_wrap_polygon_of_too_many_points_and_distances_out_of_range_are_bounded()
    {
        // 300 points (more than are read: the object's box instead), a distance far past any page and a negative one.
        var points = string.Concat(Enumerable.Range(0, 300).Select(i => $"<wp:lineTo x=\"{i * 72}\" y=\"{i % 2 * 21600}\"/>"));
        var shape = Anchor(ShapeUri, Rectangle, 1080000, 720000, Column, Paragraph0,
            $"<wp:wrapTight wrapText=\"bothSides\" distL=\"99999999999999\" distR=\"-5\"><wp:wrapPolygon edited=\"0\"><wp:start x=\"0\" y=\"0\"/>{points}</wp:wrapPolygon></wp:wrapTight>");

        var read = Assert.Single(Objects(Read(new DocxBuilder().Paragraph(P(shape + R("text"))))).OfType<HShape>());

        Assert.Null(read.Anchor!.WrapPolygon);
        Assert.Equal((HwpxUnits.FromEmu(20_000_000), 0), (read.OuterMargin.Left, read.OuterMargin.Right));
        // Laid out, the text goes below the object in no time.
        SkipWithoutFonts();
        var docx = new DocxBuilder().Paragraph(P(shape + R(string.Join(' ', Enumerable.Repeat("word", 50)))));
        Assert.NotEmpty(ComposeDocx(docx));
    }

    [Fact]
    public void Word_vml_shapes_bring_their_wrap_side_and_distance_from_the_text_9_pt_beside_them_by_default()
    {
        var shape = "<w:r><w:pict><v:rect style=\"position:absolute;margin-left:0;margin-top:0;width:50pt;height:40pt;z-index:1;" +
                    "mso-wrap-distance-left:4pt;mso-wrap-distance-bottom:2pt;mso-position-horizontal-relative:margin;mso-position-vertical-relative:text\" " +
                    "fillcolor=\"black\"><w10:wrap type=\"square\" side=\"right\"/></v:rect></w:pict></w:r>";

        var read = Assert.Single(Objects(Read(new DocxBuilder().Paragraph(P(shape)))).OfType<HShape>());

        Assert.Equal(("SQUARE", "RIGHT_ONLY"), (read.Anchor!.Wrap, read.Anchor.TextFlow));
        Assert.Equal(new HInsets(400, 900, 0, 200), read.OuterMargin);
    }

    [Fact]
    public void A_word_picture_keeps_its_distance_from_the_text_beside_it()
    {
        // A 3 cm shape at the column's left edge (from x 72) with distR 0.5 cm: the lines beside it start 0.5 cm right
        // of it, those below it at the margin.
        var shape = Anchor(ShapeUri, Rectangle, 1080000, 1080000, Column, Paragraph0,
            "<wp:wrapSquare wrapText=\"bothSides\" distT=\"0\" distB=\"0\" distL=\"0\" distR=\"180000\"/>");
        var docx = new DocxBuilder().Paragraph(P(shape + R(string.Join(' ', Enumerable.Repeat("word", 120)))));

        var page = ComposeDocx(docx)[0];

        var objectRight = 72 + 1080000 / 12700f;
        var rows = Rows(page);
        var beside = rows.Where(r => r.Baseline < 72 + 1080000 / 12700f).ToList();
        Assert.NotEmpty(beside);
        Assert.All(beside, r => Assert.Equal(objectRight + 180000 / 12700f, r.Runs.Min(run => run.Positions[0].X), 0.5f));
        Assert.Contains(rows, r => r.Baseline > 72 + 1080000 / 12700f + 20 && Math.Abs(r.Runs.Min(run => run.Positions[0].X) - 72) < 0.5f);
    }

    [Fact]
    public void A_word_picture_at_the_bottom_right_of_the_margins_goes_into_that_corner()
    {
        // A 2 × 2 cm black square aligned right and bottom against the margins (as a verification QR code is placed):
        // its corner at the margins' corner (A4, 1 inch margins: 523.3, 769.9).
        var shape = Anchor(ShapeUri, Rectangle, 720000, 720000,
            "<wp:positionH relativeFrom=\"margin\"><wp:align>right</wp:align></wp:positionH>",
            "<wp:positionV relativeFrom=\"margin\"><wp:align>bottom</wp:align></wp:positionV>", "<wp:wrapSquare wrapText=\"bothSides\"/>");

        var page = ComposeDocx(new DocxBuilder().Paragraph(P(shape + R("Body"))))[0];

        var (right, bottom) = (11906 / 20f - 72, 16838 / 20f - 72);
        var size = 720000 / 12700f;
        using var pixels = Pixels(page);
        Assert.True(Dark(pixels, (int)(right - 2), (int)(bottom - 2)));
        Assert.True(Dark(pixels, (int)(right - size + 2), (int)(bottom - size + 2)));
        Assert.False(Dark(pixels, (int)(right + 2), (int)(bottom - 2)));
        Assert.False(Dark(pixels, (int)(right - 2), (int)(bottom + 2)));
        Assert.False(Dark(pixels, (int)(right - size - 2), (int)(bottom - size / 2)));
    }

    // ───────────────────────── 한글 documents ─────────────────────────

    [Fact]
    public void Hangul_objects_bring_their_text_flow_and_outer_margin_for_layout()
    {
        var path = Path.Combine(_folder, "flow.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(_folder), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            // The first picture: its text on its wider side only, 2 mm from it on the left and 3 mm on the right.
            var picture = xml.IndexOf("binaryItemIDRef=\"image1\"", StringComparison.Ordinal);
            var start = xml.LastIndexOf("<hp:pic ", picture, StringComparison.Ordinal);
            var margin = xml.IndexOf("<hp:outMargin", picture, StringComparison.Ordinal);
            var marginEnd = xml.IndexOf("/>", margin, StringComparison.Ordinal) + 2;
            xml = xml[..start] + xml[start..margin].Replace("textFlow=\"BOTH_SIDES\"", "textFlow=\"LARGEST_ONLY\"") +
                  "<hp:outMargin left=\"567\" right=\"850\" top=\"0\" bottom=\"283\"/>" + xml[marginEnd..];
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        var picture0 = HwpxReader.ReadForLayout(path, Path.Combine(_folder, "media")).Sections[0].Blocks.OfType<HParagraph>()
            .SelectMany(p => p.Inlines).OfType<HImage>().First();

        Assert.Equal(("SQUARE", "LARGEST_ONLY"), (picture0.Anchor!.Wrap, picture0.Anchor.TextFlow));
        Assert.Equal(new HInsets(567, 850, 0, 283), picture0.OuterMargin);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private const string Column = "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>";
    private const string Paragraph0 = "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>";

    /// <summary>A black rectangle (wps:wsp content).</summary>
    private const string Rectangle =
        "<wps:wsp><wps:cNvSpPr/><wps:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill>" +
        "<a:ln><a:noFill/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";

    private static HAnchor Floating(string wrap, string flow = "BOTH_SIDES", string align = "LEFT", int offset = 0) =>
        new("COLUMN", align, offset, "PARA", "TOP", 0, wrap, true) { TextFlow = flow };

    /// <summary>
    /// A black rectangle <paramref name="width"/> × <paramref name="height"/> (HWPUNIT) floating by <paramref name="anchor"/>,
    /// <paramref name="distance"/> from the text on its left and right (below it when <paramref name="bottom"/>).
    /// </summary>
    private static HShape Box(int width, int height, HAnchor anchor, int distance = 0, bool bottom = false) =>
        new(HShapeKind.Rectangle)
        {
            Width = width,
            Height = height,
            Fill = "#000000",
            Line = HBorder.None,
            Anchor = anchor,
            OuterMargin = bottom ? new HInsets(0, 0, 0, distance) : new HInsets(distance, distance, 0, 0),
        };

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    private static HParagraph Paragraph(string text)
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        paragraph.Inlines.Add(new HText(text, Format()));
        return paragraph;
    }

    private List<PageContent> Compose(HParagraph paragraph, HPage? page = null)
    {
        SkipWithoutFonts();
        var section = new HSection { Page = page ?? Wide };
        section.Blocks.Add(paragraph);
        var document = new HDocument();
        document.Sections.Add(section);
        return new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, TestContext.Current.CancellationToken).Pages;
    }

    private HDocument Read(DocxBuilder docx) =>
        DocxReader.Read(docx.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx")), Path.Combine(_folder, "media"));

    private List<PageContent> ComposeDocx(DocxBuilder docx)
    {
        SkipWithoutFonts();
        return new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(Read(docx), TestContext.Current.CancellationToken).Pages;
    }

    private static IEnumerable<HInline> Objects(HDocument document) =>
        document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines);

    private void SkipWithoutFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    /// <summary>The glyph runs of a page by their baseline, top to bottom.</summary>
    private static List<(float Baseline, List<GlyphRun> Runs)> Rows(PageContent page) =>
        [.. Runs(page).Where(r => r.Text.Trim().Length > 0).GroupBy(r => MathF.Round(r.Positions[0].Y, 1)).OrderBy(g => g.Key).Select(g => (g.Key, g.ToList()))];

    private static string Normalise(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    private static SKBitmap Pixels(PageContent page)
    {
        var bitmap = new SKBitmap((int)Math.Ceiling(page.Width), (int)Math.Ceiling(page.Height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        page.Draw(canvas);
        return bitmap;
    }

    private static bool Dark(SKBitmap pixels, int x, int y) => pixels.GetPixel(x, y).Red < 100;
}
