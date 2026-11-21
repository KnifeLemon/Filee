// Vertical text (Typesetting/VerticalText.cs) in Word table cells and text boxes, 한글 cells and slide text boxes;
// SmartArt drawn from the drawing Word saves with it; a 한글 check box that is on; Markdown's backslashes in the
// template's Korean font. Documents are built in code and laid
// out with the repository's Noto fonts; the assertions look at where the glyphs land, which every computer agrees on.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class VerticalTextTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private const string NotoStyles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Noto Sans" w:hAnsi="Noto Sans" w:eastAsia="Noto Sans KR" w:cs="Noto Sans Arabic"/>
        <w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblCellMar>
          <w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        """;

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void NeedFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean), "Noto Sans and Noto Sans KR not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    /// <summary>A glyph drawn on a page: the character its cluster starts at, its origin and its up direction on the page.</summary>
    private sealed record Glyph(char Character, SKPoint Origin, SKPoint Up);

    private static List<Glyph> Glyphs(PageContent page)
    {
        var glyphs = new List<Glyph>();
        foreach (var run in page.Items.OfType<GlyphItem>().Select(i => i.Run))
        {
            var matrix = run.Transform ?? SKMatrix.Identity;
            var up = matrix.MapVector(new SKPoint(0, -1));
            for (var i = 0; i < run.Glyphs.Length; i++)
                glyphs.Add(new Glyph(run.Text[run.Clusters[i]], matrix.MapPoint(run.Positions[i]), up));
        }
        return glyphs;
    }

    private static bool Upright(Glyph glyph) => Math.Abs(glyph.Up.X) < 0.01f && glyph.Up.Y < -0.99f;

    /// <summary>Turned 90° clockwise: the glyph's top points right.</summary>
    private static bool TurnedDown(Glyph glyph) => glyph.Up.X > 0.99f && Math.Abs(glyph.Up.Y) < 0.01f;

    /// <summary>Turned 270°: the glyph's top points left.</summary>
    private static bool TurnedUp(Glyph glyph) => glyph.Up.X < -0.99f && Math.Abs(glyph.Up.Y) < 0.01f;

    private async Task<List<PageContent>> ComposeAsync(string path, string format)
    {
        var document = await DocumentReaders.ReadAsync(path, format, fx.NewFolder(), Token);
        return new PageComposer(new FontResolver(_fonts), _shaper, document.Kinsoku, StyleResolver.FromTemplate()).Compose(document, Token).Pages;
    }

    private static string Cell(string direction, string text) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"1200\" w:type=\"dxa\"/><w:textDirection w:val=\"{direction}\"/></w:tcPr>{P(R(text))}</w:tc>";

    [Fact]
    public async Task Word_cells_written_top_to_bottom_stand_hangul_upright_and_turn_latin()
    {
        NeedFonts();
        var table = "<w:tbl><w:tblGrid><w:gridCol w:w=\"1200\"/><w:gridCol w:w=\"1200\"/></w:tblGrid><w:tr>" +
                    Cell("tbRl", "세로AB") + Cell("btLr", "Up") + "</w:tr></w:tbl>";
        var builder = new DocxBuilder { Styles = NotoStyles }.Paragraph(table);
        var source = builder.Save(Path.Combine(fx.NewFolder(), "cells.docx"));

        var read = DocxReader.Read(source, Path.Combine(fx.NewFolder(), "media"));
        var cells = read.Sections[0].Blocks.OfType<HTable>().Single().Rows[0].Cells;
        Assert.Equal([HTextFlow.Vertical, HTextFlow.Up], cells.Select(c => c.TextFlow));
        Assert.True(read.LeftOut.IsEmpty);

        var glyphs = Glyphs((await ComposeAsync(source, "docx"))[0]);
        var hangul = glyphs.Where(g => g.Character is '세' or '로').ToList();
        var latin = glyphs.Where(g => g.Character is 'A' or 'B').ToList();
        var up = glyphs.Where(g => g.Character is 'U' or 'p').ToList();
        Assert.Equal(2, hangul.Count);
        Assert.All(hangul, g => Assert.True(Upright(g), $"{g.Character} up {g.Up}"));
        Assert.All(latin, g => Assert.True(TurnedDown(g), $"{g.Character} up {g.Up}"));
        Assert.All(up, g => Assert.True(TurnedUp(g), $"{g.Character} up {g.Up}"));
        // Top to bottom in one column: 세 above 로 above A above B; the bottom-to-top cell reads upwards.
        var down = hangul.Concat(latin).ToList();
        Assert.True(down.Zip(down.Skip(1)).All(pair => pair.Second.Origin.Y > pair.First.Origin.Y), string.Join(", ", down.Select(g => g.Origin)));
        Assert.True(up[1].Origin.Y < up[0].Origin.Y);
        // Each cell's text stays inside its column (1200 twips = 60 pt wide), the second right of the first.
        Assert.InRange(down.Max(g => g.Origin.X) - down.Min(g => g.Origin.X), 0, 20);
        Assert.True(up.Min(g => g.Origin.X) > down.Max(g => g.Origin.X));
    }

    /// <summary>A floating text box (wps:wsp) 60 × 140 pt with its text written as <paramref name="vert"/> says.</summary>
    private static string TextBox(string vert, string text) => Anchor(ShapeUri,
        "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"762000\" cy=\"1778000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr>" +
        $"<wps:txbx><w:txbxContent>{P(R(text))}</w:txbxContent></wps:txbx><wps:bodyPr vert=\"{vert}\"/></wps:wsp>",
        762000, 1778000, "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>",
        "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>");

    [Fact]
    public async Task A_word_text_box_written_east_asian_style_is_drawn_vertically_and_not_left_to_libreoffice()
    {
        NeedFonts();
        var source = new DocxBuilder { Styles = NotoStyles }.Paragraph(P(R("Box") + TextBox("eaVert", "가나Hi")))
            .Save(Path.Combine(fx.NewFolder(), "box.docx"));

        var box = DocxReader.Read(source, Path.Combine(fx.NewFolder(), "media")).Sections[0].Blocks.OfType<HParagraph>()
            .SelectMany(p => p.Inlines).OfType<HTextBox>().Single();
        Assert.Equal(HTextFlow.Vertical, box.TextFlow);
        Assert.Null(new FileeRendererConverter().FirstUnsupported(source, "docx"));

        var glyphs = Glyphs((await ComposeAsync(source, "docx"))[0]);
        Assert.All(glyphs.Where(g => g.Character is '가' or '나'), g => Assert.True(Upright(g)));
        Assert.All(glyphs.Where(g => g.Character is 'H' or 'i'), g => Assert.True(TurnedDown(g)));
        var inBox = glyphs.Where(g => g.Character is '가' or '나' or 'H' or 'i').ToList();
        Assert.Equal(4, inBox.Count);
        Assert.True(inBox.Zip(inBox.Skip(1)).All(pair => pair.Second.Origin.Y > pair.First.Origin.Y));
    }

    [Fact]
    public void A_slide_text_box_in_east_asian_vertical_writing_stands_its_hangul_upright()
    {
        NeedFonts();
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.TextBox(100, 50, 80, 300, SlideDeckBuilder.Paragraph("세로AB", rPr: "<a:rPr lang=\"ko-KR\" sz=\"2400\"><a:latin typeface=\"Noto Sans\"/><a:ea typeface=\"Noto Sans KR\"/></a:rPr>"),
            "<a:bodyPr vert=\"eaVert\" wrap=\"square\"><a:noAutofit/></a:bodyPr>"));
        var path = deck.Save(Path.Combine(fx.NewFolder(), "vertical.pptx"));
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "pptx"));

        var read = PptxSlideReader.Read(path, cancellationToken: Token);
        using var renderer = new SlideRenderer(new FontResolver(_fonts), _shaper, Token);
        var glyphs = Glyphs(renderer.Render(read, read.Slides[0]));
        var text = glyphs.Where(g => g.Character is '세' or '로' or 'A' or 'B').ToList();
        Assert.Equal(4, text.Count);
        Assert.All(text.Take(2), g => Assert.True(Upright(g), $"{g.Character} up {g.Up}"));
        Assert.All(text.Skip(2), g => Assert.True(TurnedDown(g), $"{g.Character} up {g.Up}"));
        Assert.True(text.Zip(text.Skip(1)).All(pair => pair.Second.Origin.Y > pair.First.Origin.Y));
    }

    [Fact]
    public void Upright_glyphs_get_their_own_part_of_the_text_and_brackets_stay_turned()
    {
        NeedFonts();
        const string text = "가「A」。";
        var font = new FontResolver(_fonts).Find(RepositoryFonts.Korean, FontStyle.Regular)!.Font;
        var shaped = _shaper.Shape(font, text);
        var run = shaped.ToGlyphRun(font, 10, new SKPoint(0, 10), text);

        var parts = VerticalText.Split(run, UprightGlyphs.EastAsian).ToList();

        Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
        Assert.Equal(["가", "「A」", "。"], parts.Select(p => p.Text));
        Assert.Equal([true, false, true], parts.Select(p => p.Transform is not null));
        Assert.All(parts, p => p.Validate());
        Assert.Single(VerticalText.Split(run, UprightGlyphs.None));
        Assert.Equal(text.Length, VerticalText.Split(run, UprightGlyphs.All).Count());
    }

    [Fact]
    public void Hangul_cells_and_text_boxes_written_vertically_are_drawn_so_and_a_check_box_shows_it_is_on()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "vertical.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), path);
        const string SubList = "<hp:subList id=\"\" textDirection=\"VERTICAL\" lineWrap=\"BREAK\" vertAlign=\"TOP\" linkListIDRef=\"0\" linkListNextIDRef=\"0\" textWidth=\"0\" textHeight=\"0\" hasTextRef=\"0\" hasNumRef=\"0\">" +
            "<hp:p id=\"0\" paraPrIDRef=\"0\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\"><hp:run charPrIDRef=\"0\"><hp:t>세로</hp:t></hp:run></hp:p></hp:subList>";
        const string Table =
            "<hp:tbl id=\"90\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"0\" rowCnt=\"1\" colCnt=\"1\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"3000\" widthRelTo=\"ABSOLUTE\" height=\"8000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" +
            "<hp:pos treatAsChar=\"1\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"0\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/>" +
            "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:tr><hp:tc name=\"\" header=\"0\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"1\">" + SubList +
            "<hp:cellAddr colAddr=\"0\" rowAddr=\"0\"/><hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"3000\" height=\"8000\"/><hp:cellMargin left=\"141\" right=\"141\" top=\"141\" bottom=\"141\"/></hp:tc></hp:tr></hp:tbl>";
        const string Check =
            "<hp:checkBtn caption=\"동의\" value=\"CHECKED\" backStyle=\"TRANSPARENT\" name=\"C\" foreColor=\"#000000\" backColor=\"#FFFFFF\" groupName=\"\" tabStop=\"1\" editable=\"1\" tabOrder=\"1\" enabled=\"1\" " +
            "borderTypeIDRef=\"0\" drawFrame=\"1\" printable=\"1\" command=\"\"><hp:formCharPr charPrIDRef=\"0\" followContext=\"0\" autoSz=\"0\" wordWrap=\"0\"/>" +
            "<hp:sz width=\"7087\" widthRelTo=\"ABSOLUTE\" height=\"1984\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" +
            "<hp:pos treatAsChar=\"1\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"1\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/>" +
            "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:checkBtn>";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            xml = xml.Replace("<hp:t>줄 간격</hp:t>", "<hp:t>줄 간격</hp:t>" + Table + Check, StringComparison.Ordinal);
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        var document = HwpxReader.ReadForLayout(path, Path.Combine(folder, "media"));
        var inlines = document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).ToList();
        var cell = document.Sections[0].Blocks.OfType<HTable>().Single(t => t.Rows is [{ Cells: [{ Blocks: [HParagraph] }] }]
            && Text(t.Rows[0].Cells[0].Blocks) == "세로").Rows[0].Cells[0];
        Assert.Equal(HTextFlow.Vertical, cell.TextFlow);
        Assert.Equal(0, document.LeftOut[HLeftOutKind.VerticalText]);
        Assert.NotEqual("vertical text", new FileeRendererConverter().FirstUnsupported(path, "hwpx"));

        // The check box that is on: its square holds a check mark, then the caption.
        var control = inlines.OfType<HTextBox>().Single(b => b.Width == 7087);
        var mark = Assert.IsType<HTextBox>(((HParagraph)control.Blocks[0]).Inlines[0]);
        Assert.Equal(("✓", HShapeKind.Rectangle), (Text(mark.Blocks), mark.Shape));
        Assert.EndsWith("동의", Text(control.Blocks));
    }

    private static string Text(IEnumerable<HBlock> blocks) =>
        string.Concat(blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));

    /// <summary>The repository's fonts, with Noto Sans KR also answering to 맑은 고딕 (the template's font), as Malgun Gothic would.</summary>
    private sealed class MalgunAsNoto(RepositoryFonts fonts) : IFontCollection
    {
        public IReadOnlyList<FontFace> Family(string name) =>
            name is "맑은 고딕" or "Malgun Gothic" ? fonts.Family(RepositoryFonts.Korean) : fonts.Family(name);

        public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) => fonts.ForCharacter(codePoint, style, language);
    }

    [Fact]
    public async Task Markdown_backslashes_in_the_template_s_korean_font_are_drawn_by_a_latin_font()
    {
        NeedFonts();
        var path = Path.Combine(fx.NewFolder(), "formula.md");
        await File.WriteAllTextAsync(path, "한글 *기울임 \\alpha* C:\\Users\n", new UTF8Encoding(false), Token);
        var document = await DocumentReaders.ReadAsync(path, "md", fx.NewFolder(), Token);

        var page = new PageComposer(new FontResolver(new MalgunAsNoto(_fonts)), _shaper, document.Kinsoku, StyleResolver.FromTemplate()).Compose(document, Token).Pages[0];

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var korean = _fonts.Font(RepositoryFonts.Korean);
        Assert.Contains(runs, r => r.Font == korean && r.Text.Contains('한'));
        var backslashes = runs.Where(r => r.Text.Contains('\\')).ToList();
        Assert.Equal(2, backslashes.Sum(r => r.Text.Count(c => c == '\\')));
        Assert.All(backslashes, r => Assert.Same(_fonts.Font(RepositoryFonts.Latin), r.Font));
    }

    // ───────────────────────── SmartArt ─────────────────────────

    private const string Diagram =
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"3600000\" cy=\"2000000\"/><wp:docPr id=\"9\" name=\"Diagram\"/><a:graphic>" +
        "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">" +
        "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rId91\" r:lo=\"rId92\" r:qs=\"rId93\" r:cs=\"rId94\"/>" +
        "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    /// <summary>
    /// Adds a SmartArt data part (rId91) and, with <paramref name="drawing"/>, the drawing Word saves of it (rId99,
    /// named by the data part's dsp:dataModelExt): two boxes with text.
    /// </summary>
    private static void AddDiagram(string path, bool drawing)
    {
        const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        const string Dsp = "xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\"";
        static string Box(int x, string text) =>
            $"<dsp:sp modelId=\"{{{x}}}\"><dsp:nvSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr>" +
            $"<a:xfrm><a:off x=\"{x}\" y=\"500000\"/><a:ext cx=\"1500000\" cy=\"1000000\"/></a:xfrm><a:prstGeom prst=\"roundRect\"><a:avLst/></a:prstGeom>" +
            "<a:solidFill><a:srgbClr val=\"4472C4\"/></a:solidFill></dsp:spPr><dsp:txBody><a:bodyPr anchor=\"ctr\"/><a:lstStyle/>" +
            $"<a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"en-US\" sz=\"1800\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:latin typeface=\"Noto Sans\"/></a:rPr><a:t>{text}</a:t></a:r></a:p></dsp:txBody>" +
            $"<dsp:txXfrm><a:off x=\"{x}\" y=\"500000\"/><a:ext cx=\"1500000\" cy=\"1000000\"/></dsp:txXfrm></dsp:sp>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        void Write(string name, string xml)
        {
            zip.GetEntry(name)?.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        string rels;
        using (var reader = new StreamReader(zip.GetEntry("word/_rels/document.xml.rels")!.Open()))
            rels = reader.ReadToEnd();
        var added = "<Relationship Id=\"rId91\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData\" Target=\"diagrams/data1.xml\"/>" +
                    (drawing ? "<Relationship Id=\"rId99\" Type=\"http://schemas.microsoft.com/office/2007/relationships/diagramDrawing\" Target=\"diagrams/drawing1.xml\"/>" : "");
        Write("word/_rels/document.xml.rels", rels.Replace("</Relationships>", added + "</Relationships>", StringComparison.Ordinal));
        Write("word/diagrams/data1.xml", $"<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" {A}><dgm:ptLst/>" +
            (drawing ? $"<dgm:extLst><a:ext uri=\"http://schemas.microsoft.com/office/drawing/2008/diagram\"><dsp:dataModelExt {Dsp} relId=\"rId99\" minVer=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/></a:ext></dgm:extLst>" : "") +
            "</dgm:dataModel>");
        if (drawing)
        {
            Write("word/diagrams/drawing1.xml", $"<dsp:drawing {Dsp} {A}><dsp:spTree><dsp:nvGrpSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr><dsp:grpSpPr/>" +
                Box(100000, "Plan") + Box(2000000, "Ship") + "</dsp:spTree></dsp:drawing>");
        }
    }

    [Fact]
    public async Task Smartart_is_drawn_from_the_drawing_word_saved_with_it()
    {
        NeedFonts();
        var folder = fx.NewFolder();
        var source = new DocxBuilder { Styles = NotoStyles }.Paragraph(P(Diagram)).Save(Path.Combine(folder, "smartart.docx"));
        AddDiagram(source, drawing: true);
        var bare = new DocxBuilder { Styles = NotoStyles }.Paragraph(P(Diagram)).Save(Path.Combine(folder, "bare.docx"));
        AddDiagram(bare, drawing: false);

        var document = DocxReader.Read(source, Path.Combine(folder, "media"));
        var picture = document.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.NotNull(picture.Drawing);
        Assert.True(File.Exists(picture.Path)); // the writers' picture of it
        Assert.Equal(0, document.LeftOut[HLeftOutKind.SmartArt]);
        Assert.Equal(1, DocxReader.Read(bare, Path.Combine(folder, "media2")).LeftOut[HLeftOutKind.SmartArt]);
        var renderer = new FileeRendererConverter();
        Assert.Null(renderer.FirstUnsupported(source, "docx"));
        Assert.Equal("SmartArt", renderer.FirstUnsupported(bare, "docx"));

        // Drawn as text in the frame (283.5 × 157.5 pt at the left margin), the boxes side by side.
        var glyphs = Glyphs((await ComposeAsync(source, "docx"))[0]);
        var plan = glyphs.Where(g => g.Character == 'P').ToList();
        var ship = glyphs.Where(g => g.Character == 'S').ToList();
        Assert.Single(plan);
        Assert.Single(ship);
        Assert.True(ship[0].Origin.X > plan[0].Origin.X + 60, $"{plan[0].Origin} {ship[0].Origin}");
        Assert.InRange(plan[0].Origin.X, 72, 72 + 283.5f);
    }
}
