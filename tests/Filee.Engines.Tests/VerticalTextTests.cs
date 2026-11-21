// Vertical text (Typesetting/VerticalText.cs) in Word table cells and text boxes, 한글 cells and slide text boxes, and
// sections written vertically (Word, 한글, ODT pages), with the font's vertical forms and numbers set across; SmartArt
// drawn from the drawing Word saves with it; a 한글 check box that is on; backslashes in the template's Korean font and
// in one the document names; a rectangle path gradient. Documents are built in code and laid
// out with the repository's Noto fonts; the assertions look at where the glyphs land, which every computer agrees on.
// Pictures in vertical text (laid out like characters, and floating ones' wrap room), numbers set across taking an em,
// 한글 headers written vertically, and the seams of a rectangle gradient's pieces are checked on models built in code.

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

    // ───────────────────────── Sections written vertically ─────────────────────────

    [Fact]
    public async Task A_word_section_written_vertically_runs_its_lines_down_from_the_right_and_its_footer_across()
    {
        NeedFonts();
        var builder = new DocxBuilder { Styles = NotoStyles };
        var footer = builder.Footer(P(R("Footer")));
        builder.Paragraph(P(R("세로쓰기")) + P(R("둘째 줄")));
        builder.SectionProperties = $"<w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" + DocxBuilder.DefaultPage + "<w:textDirection w:val=\"tbRl\"/>";
        var source = builder.Save(Path.Combine(fx.NewFolder(), "vertical.docx"));
        Assert.Null(new FileeRendererConverter().FirstUnsupported(source, "docx"));
        var document = await DocumentReaders.ReadAsync(source, "docx", fx.NewFolder(), Token);
        Assert.Equal(HTextFlow.Vertical, Assert.Single(document.Sections).TextFlow);
        Assert.Equal(0, document.LeftOut[HLeftOutKind.VerticalText]);

        var page = Assert.Single(await ComposeAsync(source, "docx"));
        var glyphs = Glyphs(page);
        // The first paragraph down the right edge of the body (A4, 1 inch margins), each Hangul upright.
        var first = glyphs.Where(g => g.Character is '세' or '로' or '쓰' or '기').ToList();
        Assert.Equal(4, first.Count);
        Assert.All(first, g => Assert.True(Upright(g), $"{g.Character} up {g.Up}"));
        Assert.All(first, g => Assert.InRange(g.Origin.X, 595.3f - 72 - 20, 595.3f - 72));
        Assert.InRange(first[0].Origin.Y, 72, 72 + 20);
        Assert.True(first.Zip(first.Skip(1)).All(pair => pair.Second.Origin.Y > pair.First.Origin.Y && Math.Abs(pair.Second.Origin.X - pair.First.Origin.X) < 0.5f));
        // The next paragraph to its left; the footer across at the bottom.
        var second = glyphs.First(g => g.Character == '둘');
        Assert.True(second.Origin.X < first[0].Origin.X - 8, $"{second.Origin} {first[0].Origin}");
        var foot = glyphs.Where(g => g.Character is 'F' or 'o' or 't' or 'e' or 'r').ToList();
        Assert.NotEmpty(foot);
        Assert.All(foot, g => Assert.True(Upright(g)));
        Assert.All(foot, g => Assert.True(g.Origin.Y > 842 - 72));
    }

    [Fact]
    public async Task An_hwpx_section_written_vertically_and_an_odt_page_frame_and_cell_are_drawn_vertically()
    {
        NeedFonts();
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "vertical.hwpx");
        var across = HwpxReaderTests.HangulStylePackage(folder);
        File.Copy(across, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            xml = xml.Replace("<hp:secPr id=\"\" textDirection=\"HORIZONTAL\"", "<hp:secPr id=\"\" textDirection=\"VERTICAL\"", StringComparison.Ordinal);
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        // The section written vertically doesn't send the file elsewhere (the package's chart still does).
        var renderer = new FileeRendererConverter();
        Assert.Equal(renderer.FirstUnsupported(across, "hwpx"), renderer.FirstUnsupported(path, "hwpx"));
        var document = HwpxReader.ReadForLayout(path, Path.Combine(folder, "media"));
        Assert.Equal(HTextFlow.Vertical, document.Sections[0].TextFlow);
        Assert.Equal(0, document.LeftOut[HLeftOutKind.VerticalText]);
        var page = new PageComposer(new FontResolver(_fonts), _shaper, document.Kinsoku).Compose(document, Token).Pages[0];
        var hangul = Glyphs(page).Where(g => g.Character == '줄').ToList();
        Assert.NotEmpty(hangul);
        Assert.All(hangul, g => Assert.True(Upright(g), $"up {g.Up}"));
        Assert.True(hangul.Max(g => g.Origin.X) > page.Width / 2, "the first lines are at the right");

        // ODT: the page, a text frame and a cell written top to bottom.
        var odt = new OdtBuilder
        {
            StylesAutomatic = "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" " +
                              "fo:margin-top=\"2cm\" fo:margin-bottom=\"2cm\" fo:margin-left=\"2cm\" fo:margin-right=\"2cm\" style:writing-mode=\"tb-rl\"/></style:page-layout>",
            MasterStyles = "<style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/>",
            ContentStyles = "<style:style style:name=\"fr1\" style:family=\"graphic\"><style:graphic-properties style:writing-mode=\"tb-rl\"/></style:style>" +
                            "<style:style style:name=\"ce1\" style:family=\"table-cell\"><style:table-cell-properties style:writing-mode=\"tb-rl\"/></style:style>",
            Body = "<text:p>縦書き<draw:frame draw:style-name=\"fr1\" text:anchor-type=\"as-char\" svg:width=\"2cm\" svg:height=\"4cm\"><draw:text-box><text:p>枠</text:p></draw:text-box></draw:frame></text:p>" +
                   "<table:table><table:table-column/><table:table-row><table:table-cell table:style-name=\"ce1\"><text:p>セル</text:p></table:table-cell></table:table-row></table:table>",
        };
        var odtPath = odt.Save(Path.Combine(fx.NewFolder(), "vertical.odt"));
        Assert.Null(new FileeRendererConverter().FirstUnsupported(odtPath, "odt"));
        var read = await DocumentReaders.ReadAsync(odtPath, "odt", fx.NewFolder(), Token);
        var section = Assert.Single(read.Sections);
        Assert.Equal(HTextFlow.Vertical, section.TextFlow);
        Assert.Equal(HTextFlow.Vertical, section.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HTextBox>().Single().TextFlow);
        Assert.Equal(HTextFlow.Vertical, section.Blocks.OfType<HTable>().Single().Rows[0].Cells[0].TextFlow);
        Assert.True(read.LeftOut.IsEmpty);
    }

    [Fact]
    public void An_odp_frame_written_top_to_bottom_stands_its_cjk_upright()
    {
        var builder = new OdpBuilder
        {
            ContentStyles = OdpBuilder.GraphicStyle("gr1", "draw:fill=\"none\" draw:stroke=\"none\" style:writing-mode=\"tb-rl\""),
            Pages = OdpBuilder.Page("<draw:frame draw:style-name=\"gr1\" svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"3cm\" svg:height=\"10cm\"><draw:text-box><text:p>縦書き</text:p></draw:text-box></draw:frame>"),
        };
        var path = builder.Save(Path.Combine(fx.NewFolder(), "vertical.odp"));
        var shape = Assert.Single(Filee.Engines.Odf.Draw.OdfDrawReader.Read(path, cancellationToken: Token).Slides).Elements.OfType<SlideShape>().Single();
        Assert.Equal((Filee.Engines.Slides.TextDirection.Down, UprightGlyphs.EastAsian), (shape.Text!.Direction, shape.Text.Upright));
    }

    // ───────────────────────── Vertical forms and numbers set across ─────────────────────────

    [Fact]
    public void Two_or_three_digits_on_their_own_are_set_across_in_one_character_s_room()
    {
        NeedFonts();
        var font = new FontResolver(_fonts).Find(RepositoryFonts.Korean, FontStyle.Regular)!.Font;
        GlyphRun Run(string text, bool across = false) => _shaper.Shape(font, text).ToGlyphRun(font, 10, new SKPoint(0, 10), text) with { Across = across };

        var parts = VerticalText.Split(Run("第12回"), UprightGlyphs.EastAsian, _shaper).ToList();
        Assert.Equal(["第", "12", "回"], parts.Select(p => p.Text));
        Assert.All(parts, p => Assert.NotNull(p.Transform));
        // Across: upright (its top up the page once the text is turned), within an em.
        var digits = parts[1];
        Assert.InRange(digits.ScaleX, 0.5f, 1);
        Assert.InRange(digits.Positions[^1].X - digits.Positions[0].X, 0, 10);

        // Four digits, digits after a letter and a number the run's neighbour goes on with lie on their side.
        var year = VerticalText.Split(Run("第2024回"), UprightGlyphs.EastAsian, _shaper).ToList();
        Assert.Equal(["第", "2024", "回"], year.Select(p => p.Text));
        Assert.Null(year[1].Transform);
        Assert.Single(VerticalText.Split(Run("A12"), UprightGlyphs.EastAsian, _shaper));
        Assert.Single(VerticalText.Split(Run("12"), UprightGlyphs.EastAsian, _shaper, before: '3'));
        Assert.Single(VerticalText.Split(Run("12"), UprightGlyphs.None, _shaper));
        // A run the document sets across (Word w:eastAsianLayout w:vert): all of it, squeezed to an em.
        var marked = Assert.Single(VerticalText.Split(Run("ABCD", across: true), UprightGlyphs.EastAsian, _shaper));
        Assert.NotNull(marked.Transform);
        Assert.True(marked.ScaleX < 1);
        Assert.All(parts.Append(marked), p => p.Validate());
    }

    [Fact]
    public void Upright_characters_take_the_font_s_vertical_forms()
    {
        NeedFonts();
        var font = new FontResolver(_fonts).Find(RepositoryFonts.Korean, FontStyle.Regular)!.Font;
        const string text = "가「나」、";
        var run = _shaper.Shape(font, text).ToGlyphRun(font, 10, new SKPoint(0, 10), text);
        Assert.True(_shaper.Vertical(font, "「") is { Substituted: true, Metrics: true }, "Noto Sans KR has vert and vmtx");
        Assert.False(_shaper.Vertical(font, "가")!.Value.Substituted);

        var parts = VerticalText.Split(run, UprightGlyphs.EastAsian, _shaper).ToList();

        // The brackets stand in their vertical forms; Hangul keeps its glyphs; the comma takes its vertical form, which
        // the font puts in the corner itself (no shift added: the turn only).
        Assert.Equal(["가", "「", "나", "」", "、"], parts.Select(p => p.Text));
        Assert.All(parts, p => Assert.NotNull(p.Transform));
        Assert.Equal(run.Glyphs[0], parts[0].Glyphs[0]);
        Assert.NotEqual(run.Glyphs[1], parts[1].Glyphs[0]);
        Assert.NotEqual(run.Glyphs[4], parts[4].Glyphs[0]);
        var turn = parts[4].Transform!.Value;
        var corner = SKMatrix.CreateRotationDegrees(-90, run.Positions[4].X + 5, 10 - 3.8f);
        Assert.Equal(corner.TransX, turn.TransX, 1);
        Assert.Equal(corner.TransY, turn.TransY, 1);
        Assert.All(parts, p => p.Validate());
    }

    [Fact]
    public async Task A_word_run_that_names_the_korean_font_keeps_its_won_sign_for_a_backslash()
    {
        NeedFonts();
        var styles = NotoStyles.Replace("w:ascii=\"Noto Sans\" w:hAnsi=\"Noto Sans\" w:eastAsia=\"Noto Sans KR\"",
            "w:ascii=\"맑은 고딕\" w:hAnsi=\"맑은 고딕\" w:eastAsia=\"맑은 고딕\"", StringComparison.Ordinal);
        var source = new DocxBuilder { Styles = styles }.Paragraph(P(R("C:\\Users 한글"))).Save(Path.Combine(fx.NewFolder(), "named.docx"));
        var document = await DocumentReaders.ReadAsync(source, "docx", fx.NewFolder(), Token);

        var page = new PageComposer(new FontResolver(new MalgunAsNoto(_fonts)), _shaper, document.Kinsoku, StyleResolver.FromTemplate()).Compose(document, Token).Pages[0];

        // The document chose 맑은 고딕 (Noto Sans KR stands in for it here): Word draws its ₩, and so does Filee.
        var backslash = page.Items.OfType<GlyphItem>().Select(i => i.Run).Single(r => r.Text.Contains('\\'));
        Assert.Same(_fonts.Font(RepositoryFonts.Korean), backslash.Font);
    }

    // ───────────────────────── Pictures, numbers and headers in vertical text ─────────────────────────

    private static HCharFormat Korean() => new(Size: 1000, Font: RepositoryFonts.Korean, EastAsianFont: RepositoryFonts.Korean);

    /// <summary>A4 with 1 inch margins and a header area of <paramref name="header"/> (HWPUNIT).</summary>
    private static HSection Section(HTextFlow flow, int header = 0) =>
        new() { TextFlow = flow, Page = new HPage(59528, 84188, 7200, 7200, 7200, 7200, header, 0, 0) };

    /// <summary>A red picture <paramref name="width"/> × <paramref name="height"/> points.</summary>
    private string Red(float width, float height)
    {
        var path = Path.Combine(fx.NewFolder(), "red.png");
        using var bitmap = new SKBitmap((int)width, (int)height);
        bitmap.Erase(SKColors.Red);
        using (var file = File.Create(path))
            bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
        return path;
    }

    /// <summary>Where the red of a page drawn at 1 px per point is (empty for none).</summary>
    private static SKRectI RedArea(PageContent page)
    {
        using var bitmap = new SKBitmap((int)Math.Ceiling(page.Width), (int)Math.Ceiling(page.Height));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            page.Draw(canvas);
        }
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, -1, -1);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green < 80 && pixel.Blue < 80)
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
            }
        }
        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    /// <summary>The middle of each upright glyph's em box on the page (the point VerticalText turns it about).</summary>
    private static List<(char Character, SKPoint Middle)> Middles(PageContent page) =>
        [.. page.Items.OfType<GlyphItem>().Select(i => i.Run).Where(r => r.Transform is not null).SelectMany(run =>
            Enumerable.Range(0, run.Glyphs.Length).Select(g => (run.Text[run.Clusters[g]],
                run.Transform!.Value.MapPoint(run.Positions[g].X + run.Size / 2, run.Positions[g].Y - 0.38f * run.Size))))];

    [Fact]
    public void A_picture_in_vertical_text_stands_upright_and_a_number_set_across_takes_one_em()
    {
        NeedFonts();
        var document = new HDocument();
        var section = Section(HTextFlow.Vertical);
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0) { Layout = new HParaLayout(Breaking: new HLineBreaking(AutoSpaceDigits: true)) } };
        paragraph.Inlines.Add(new HText("가", Korean()));
        paragraph.Inlines.Add(new HImage(Red(40, 20)) { Width = 4000, Height = 2000 });
        paragraph.Inlines.Add(new HText("나第12回다", Korean()));
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);

        var page = Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, Token).Pages);

        // The picture stands as it is, 40 pt wide and 20 pt high, in the first line at the right.
        var red = RedArea(page);
        Assert.InRange(red.Width, 39, 41);
        Assert.InRange(red.Height, 19, 21);
        Assert.True(red.Left > page.Width / 2, $"{red}");
        // Along the line it takes its height: 나 comes 20 pt further down than an em after 가.
        var glyphs = Glyphs(page);
        var (ga, na) = (glyphs.Single(g => g.Character == '가'), glyphs.Single(g => g.Character == '나'));
        Assert.Equal(10 + 20, na.Origin.Y - ga.Origin.Y, 1.0);
        Assert.InRange(red.Top, ga.Origin.Y - 2, na.Origin.Y);
        // 第12回: the number set across takes one em between 第 and 回, without auto-spacing beside it.
        var middles = Middles(page);
        var (first, last) = (middles.Single(m => m.Character == '第').Middle, middles.Single(m => m.Character == '回').Middle);
        Assert.Equal(20, last.Y - first.Y, 0.6);
        var digits = page.Items.OfType<GlyphItem>().Select(i => i.Run).Single(r => r.Text == "12");
        Assert.NotNull(digits.Transform);
        var across = digits.Transform!.Value.MapPoint((digits.Positions[0].X + digits.Positions[^1].X) / 2 + digits.Size * 0.15f, digits.Positions[0].Y - 0.38f * digits.Size);
        Assert.InRange(across.Y, first.Y + 5, last.Y - 5);
    }

    [Fact]
    public void Text_goes_around_a_floating_picture_of_a_vertical_page_where_the_picture_stands()
    {
        NeedFonts();
        var document = new HDocument();
        var section = Section(HTextFlow.Vertical);
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0) };
        paragraph.Inlines.Add(new HImage(Red(80, 20))
        {
            Width = 8000,
            Height = 2000,
            Anchor = new HAnchor("COLUMN", "LEFT", 20000, "PARA", "TOP", 2000, "SQUARE", false),
        });
        paragraph.Inlines.Add(new HText(string.Concat(Enumerable.Repeat("가나다라마바사아자차카타파하", 40)), Korean()));
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);

        var page = new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, Token).Pages[0];

        // Upright, 80 pt wide and 20 pt high; no character's em box has its middle on it, while characters run past it
        // on both sides (above and below it, beside it to the left and right).
        var red = RedArea(page);
        Assert.InRange(red.Width, 79, 81);
        Assert.InRange(red.Height, 19, 21);
        var inside = SKRect.Inflate(new SKRect(red.Left, red.Top, red.Right, red.Bottom), -2, -2);
        var middles = Middles(page);
        Assert.DoesNotContain(middles, m => inside.Contains(m.Middle));
        Assert.Contains(middles, m => m.Middle.X > red.Left && m.Middle.X < red.Right && m.Middle.Y < red.Top);
        Assert.Contains(middles, m => m.Middle.X > red.Left && m.Middle.X < red.Right && m.Middle.Y > red.Bottom);
    }

    [Fact]
    public void A_hangul_header_written_vertically_is_set_vertically_in_its_area()
    {
        NeedFonts();
        // Read: the header keeps its direction, and isn't counted as vertical text left out.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "header.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            const string Header = "<hp:header id=\"1\" applyPageType=\"ODD\"><hp:subList id=\"\" textDirection=\"";
            Assert.Contains(Header + "HORIZONTAL", xml, StringComparison.Ordinal);
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml.Replace(Header + "HORIZONTAL", Header + "VERTICAL", StringComparison.Ordinal));
        }
        var read = HwpxReader.ReadForLayout(path, Path.Combine(folder, "media"));
        Assert.Equal(HTextFlow.Vertical, read.Sections[0].Headers.Single(h => h.Pages == HPageType.Odd).TextFlow);
        Assert.Equal(0, read.LeftOut[HLeftOutKind.VerticalText]);

        // Laid out: down its 40 pt high area from the right of the text width, each Hangul upright, the body across.
        var document = new HDocument();
        var section = Section(HTextFlow.Horizontal, header: 4000);
        var header = new HHeaderFooter(HPageType.Both) { TextFlow = HTextFlow.Vertical };
        header.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0), Inlines = { new HText("머리말", Korean()) } });
        section.Headers.Add(header);
        section.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0), Inlines = { new HText("본문", Korean()) } });
        document.Sections.Add(section);
        var page = Assert.Single(new PageComposer(new FontResolver(_fonts), _shaper).Compose(document, Token).Pages);
        var glyphs = Glyphs(page);
        var head = glyphs.Where(g => g.Character is '머' or '리' or '말').ToList();
        Assert.Equal(3, head.Count);
        Assert.All(head, g => Assert.True(Upright(g), $"{g.Character} up {g.Up}"));
        Assert.All(head, g => Assert.InRange(g.Origin.X, 595.3f - 72 - 20, 595.3f - 72));
        Assert.True(head.Zip(head.Skip(1)).All(pair => pair.Second.Origin.Y > pair.First.Origin.Y + 5 && Math.Abs(pair.Second.Origin.X - pair.First.Origin.X) < 0.5f));
        Assert.All(head, g => Assert.InRange(g.Origin.Y, 72, 72 + 40));
        var body = glyphs.Where(g => g.Character is '본' or '문').ToList();
        Assert.True(body[1].Origin.X > body[0].Origin.X + 5 && Math.Abs(body[1].Origin.Y - body[0].Origin.Y) < 0.5f, "the body runs across");
    }

    [Fact]
    public void The_pieces_of_a_rectangle_gradient_overlap_so_no_seam_shows_between_them()
    {
        // Each piece drawn smoothed on its own, as a PDF viewer draws them, at a scale that puts the edges between pixels.
        static int Seams(float overlap)
        {
            var box = new SKRect(0, 0, 100, 60);
            var focus = SlidePaint.Focus(new SKRect(0.3f, 0.4f, 0.25f, 0.35f), box);
            using var bitmap = new SKBitmap(260, 160);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            canvas.Translate(0.37f, 0.61f);
            canvas.Scale(2.53f);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRect(focus, white);
            foreach (var (piece, _, _) in SlidePaint.RectanglePieces(focus, box, overlap))
            {
                using (piece)
                    canvas.DrawPath(piece, white);
            }
            canvas.Flush();
            var seams = 0;
            // Inside the box, a pixel away from its edge.
            for (var y = 3; y < 60 * 2.53f - 2; y++)
            {
                for (var x = 3; x < 100 * 2.53f - 2; x++)
                {
                    if (bitmap.GetPixel(x, y).Red < 250)
                        seams++;
                }
            }
            return seams;
        }
        Assert.True(Seams(0) > 0, "edge to edge, smoothed pieces leave a hairline");
        Assert.Equal(0, Seams(0.5f));
    }

    // ───────────────────────── Slides: a rectangle gradient ─────────────────────────

    [Fact]
    public void A_rectangle_path_gradient_reaches_its_last_stop_at_the_box_s_edges()
    {
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ellipse = new SKPath();
        ellipse.AddOval(new SKRect(0, 0, 200, 100));
        var fill = new GradientFill([new(0, SKColors.Red), new(1, SKColors.Blue)], GradientShape.Rectangle, 0, false, new SKRect(0.5f, 0.5f, 0.5f, 0.5f));
        using (var images = new SlideImages())
            SlidePaint.Fill(canvas, ellipse, fill, new SKRect(0, 0, 200, 100), images);

        // In rectangles from the centre (red) to the box (blue): inside the ellipse near the middle of each side it is
        // nearly blue, as DrawingML and LibreOffice draw it (an ellipse through the corners left it two thirds of the way).
        static void Near(SKColor actual, SKColor expected) =>
            Assert.True(Math.Abs(actual.Red - expected.Red) < 40 && Math.Abs(actual.Blue - expected.Blue) < 40, $"{actual} vs {expected}");
        Near(bitmap.GetPixel(100, 50), SKColors.Red);
        Near(bitmap.GetPixel(195, 50), SKColors.Blue);
        Near(bitmap.GetPixel(100, 3), SKColors.Blue);
        Near(bitmap.GetPixel(150, 50), new SKColor(128, 0, 127));
        // Contours are rectangles: half way across and half way down, the same colour.
        var (across, down) = (bitmap.GetPixel(150, 50), bitmap.GetPixel(100, 75));
        Assert.InRange(Math.Abs(across.Red - down.Red), 0, 12);

        // A focus in the bottom-right corner (PowerPoint's "from the corner"): red there, blue at the far edges.
        using var box = new SKPath();
        box.AddRect(new SKRect(0, 0, 200, 100));
        canvas.Clear(SKColors.White);
        using (var images = new SlideImages())
            SlidePaint.Fill(canvas, box, fill with { Focus = new SKRect(1, 1, 0, 0) }, new SKRect(0, 0, 200, 100), images);
        Near(bitmap.GetPixel(198, 98), SKColors.Red);
        Near(bitmap.GetPixel(2, 50), SKColors.Blue);
        Near(bitmap.GetPixel(150, 1), SKColors.Blue);
    }
}
