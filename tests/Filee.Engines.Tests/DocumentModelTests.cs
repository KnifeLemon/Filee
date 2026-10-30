// The rendering detail of the document model (HwpxModel.Layout.cs): what HwpxReader and DocxReader fill in, that it
// leaves written files alone, and StyleResolver. Every input is generated here.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Typesetting;
using ImageMagick;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocumentModelTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    // ───────────────────────── HWPX packages ─────────────────────────

    private const string Namespaces =
        "xmlns:hp=\"http://www.hancom.co.kr/hwpml/2011/paragraph\" xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\" " +
        "xmlns:hc=\"http://www.hancom.co.kr/hwpml/2011/core\" xmlns:hh=\"http://www.hancom.co.kr/hwpml/2011/head\" " +
        "xmlns:hm=\"http://www.hancom.co.kr/hwpml/2011/master-page\"";

    private const string Case = "<hp:case hp:required-namespace=\"http://www.hancom.co.kr/hwpml/2016/HwpUnitChar\">";

    private static string Margins(int intent = 0, int left = 0) =>
        $"<hh:margin><hc:intent value=\"{intent}\" unit=\"HWPUNIT\"/><hc:left value=\"{left}\" unit=\"HWPUNIT\"/><hc:right value=\"0\" unit=\"HWPUNIT\"/><hc:prev value=\"0\" unit=\"HWPUNIT\"/><hc:next value=\"0\" unit=\"HWPUNIT\"/></hh:margin>";

    private static string LineSpacing(string type, int value) =>
        $"<hp:switch>{Case}{Margins()}<hh:lineSpacing type=\"{type}\" value=\"{value}\" unit=\"HWPUNIT\"/></hp:case>" +
        $"<hp:default>{Margins()}<hh:lineSpacing type=\"{type}\" value=\"{(type == "PERCENT" ? value : value * 2)}\" unit=\"HWPUNIT\"/></hp:default></hp:switch>";

    private static string CharPr(string id, string fontRef = "0", string ratio = "100", string spacing = "0", string relSz = "100", string offset = "0")
    {
        static string Slots(string name, string values)
        {
            var parts = values.Split(',');
            string Value(int i) => parts.Length == 1 ? parts[0] : parts[i];
            return $"<hh:{name} hangul=\"{Value(0)}\" latin=\"{Value(1)}\" hanja=\"{Value(2)}\" japanese=\"{Value(3)}\" other=\"{Value(4)}\" symbol=\"{Value(5)}\" user=\"{Value(6)}\"/>";
        }
        return $"<hh:charPr id=\"{id}\" height=\"1000\" textColor=\"#000000\" shadeColor=\"none\" useFontSpace=\"0\" useKerning=\"0\" symMark=\"NONE\" borderFillIDRef=\"1\">" +
               Slots("fontRef", fontRef) + Slots("ratio", ratio) + Slots("spacing", spacing) + Slots("relSz", relSz) + Slots("offset", offset) +
               "<hh:underline type=\"NONE\" shape=\"SOLID\" color=\"#000000\"/><hh:strikeout shape=\"NONE\" color=\"#000000\"/></hh:charPr>";
    }

    private static string ParaPr(string id, string spacing, string attributes = "condense=\"0\" fontLineHeight=\"0\" snapToGrid=\"1\"",
        string breaks = "breakLatinWord=\"KEEP_WORD\" breakNonLatinWord=\"KEEP_WORD\" lineWrap=\"BREAK\"", string autoSpacing = "eAsianEng=\"0\" eAsianNum=\"0\"",
        string border = "borderFillIDRef=\"1\" offsetLeft=\"0\" offsetRight=\"0\" offsetTop=\"0\" offsetBottom=\"0\" connect=\"0\" ignoreMargin=\"0\"") =>
        $"<hh:paraPr id=\"{id}\" tabPrIDRef=\"0\" {attributes} suppressLineNumbers=\"0\" checked=\"0\"><hh:align horizontal=\"JUSTIFY\" vertical=\"BASELINE\"/>" +
        "<hh:heading type=\"NONE\" idRef=\"0\" level=\"0\"/>" +
        $"<hh:breakSetting {breaks} widowOrphan=\"0\" keepWithNext=\"0\" keepLines=\"0\" pageBreakBefore=\"0\"/>" +
        $"<hh:autoSpacing {autoSpacing}/>{spacing}<hh:border {border}/></hh:paraPr>";

    private static string BorderFill(string id, string edge, string fill = "") =>
        $"<hh:borderFill id=\"{id}\" threeD=\"0\" shadow=\"0\" centerLine=\"NONE\" breakCellSeparateLine=\"0\">" +
        "<hh:slash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/><hh:backSlash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/>" +
        $"<hh:leftBorder {edge}/><hh:rightBorder {edge}/><hh:topBorder {edge}/><hh:bottomBorder {edge}/>" +
        $"<hh:diagonal type=\"SOLID\" width=\"0.1 mm\" color=\"#000000\"/>{fill}</hh:borderFill>";

    private static string Hp(string runs, string paraPr = "0", string lines = "") =>
        $"<hp:p id=\"0\" paraPrIDRef=\"{paraPr}\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\">{runs}{lines}</hp:p>";

    private static string Hr(string content, string charPr = "0") => $"<hp:run charPrIDRef=\"{charPr}\">{content}</hp:run>";

    private static string Ht(string text) => $"<hp:t>{text}</hp:t>";

    private static string SubList(string paragraphs) =>
        $"<hp:subList id=\"\" textDirection=\"HORIZONTAL\" lineWrap=\"BREAK\" vertAlign=\"TOP\" linkListIDRef=\"0\" linkListNextIDRef=\"0\" textWidth=\"0\" textHeight=\"0\" hasTextRef=\"0\" hasNumRef=\"0\">{paragraphs}</hp:subList>";

    private const string FloatingPos =
        "<hp:pos treatAsChar=\"0\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"1\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/>";

    private const string Outline =
        "<hp:lineShape color=\"#FF0000\" width=\"100\" style=\"SOLID\" endCap=\"FLAT\" headStyle=\"NORMAL\" tailStyle=\"NORMAL\" headfill=\"1\" tailfill=\"1\" headSz=\"MEDIUM_MEDIUM\" tailSz=\"MEDIUM_MEDIUM\" outlineStyle=\"NORMAL\" alpha=\"0\"/>" +
        "<hc:fillBrush><hc:winBrush faceColor=\"#00FF00\" hatchColor=\"#000000\" alpha=\"0\"/></hc:fillBrush>";

    /// <summary>A drawing object (rect, polygon, curve, arc ...) floating at the paragraph, with its own children.</summary>
    private static string Drawing(string name, string attributes, string children, string transform = "<hp:flip horizontal=\"0\" vertical=\"0\"/><hp:rotationInfo angle=\"0\" centerX=\"0\" centerY=\"0\" rotateimage=\"1\"/>", int zOrder = 0) =>
        $"<hp:{name} id=\"1\" zOrder=\"{zOrder}\" numberingType=\"NONE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"2\" {attributes}>" +
        $"<hp:offset x=\"0\" y=\"0\"/><hp:orgSz width=\"4000\" height=\"2000\"/><hp:curSz width=\"8000\" height=\"4000\"/>{transform}{Outline}{children}" +
        $"<hp:sz width=\"8000\" widthRelTo=\"ABSOLUTE\" height=\"4000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>{FloatingPos}<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:{name}>";

    /// <summary>An HWPX package: header.xml from <paramref name="refList"/>, one section, extra parts and manifest items.</summary>
    private static string Package(string path, string refList, string section, string secPrChildren = "", Dictionary<string, string>? parts = null,
        bool estimatedLines = false, byte[]? picture = null)
    {
        var header =
            $"<hh:head {Namespaces} version=\"1.4\" secCnt=\"1\"><hh:refList>" +
            "<hh:fontfaces itemCnt=\"7\">" + string.Concat(new[] { "HANGUL", "LATIN", "HANJA", "JAPANESE", "OTHER", "SYMBOL", "USER" }.Select(lang =>
                $"<hh:fontface lang=\"{lang}\" fontCnt=\"2\"><hh:font id=\"0\" face=\"{lang}-0\" type=\"TTF\" isEmbedded=\"0\"/><hh:font id=\"1\" face=\"{lang}-1\" type=\"TTF\" isEmbedded=\"0\"/></hh:fontface>")) +
            "</hh:fontfaces>" + refList +
            "<hh:styles itemCnt=\"1\"><hh:style id=\"0\" type=\"PARA\" name=\"바탕글\" engName=\"Normal\" paraPrIDRef=\"0\" charPrIDRef=\"0\" nextStyleIDRef=\"0\" langID=\"1042\" lockForm=\"0\"/></hh:styles>" +
            "</hh:refList></hh:head>";
        var secPr =
            "<hp:secPr id=\"\" textDirection=\"HORIZONTAL\" spaceColumns=\"1134\" tabStop=\"8000\" tabStopVal=\"4000\" tabStopUnit=\"HWPUNIT\" outlineShapeIDRef=\"0\" memoShapeIDRef=\"0\" textVerticalWidthHead=\"0\" masterPageCnt=\"0\">" +
            "<hp:pagePr landscape=\"WIDELY\" width=\"59528\" height=\"84188\" gutterType=\"LEFT_ONLY\"><hp:margin header=\"4252\" footer=\"4252\" gutter=\"0\" left=\"8504\" right=\"8504\" top=\"5668\" bottom=\"4252\"/></hp:pagePr>" +
            secPrChildren + "</hp:secPr>";
        var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><hs:sec {Namespaces}>{Hp(Hr(secPr))}{section}</hs:sec>";

        var items = new StringBuilder("<opf:item id=\"header\" href=\"Contents/header.xml\" media-type=\"application/xml\"/><opf:item id=\"section0\" href=\"Contents/section0.xml\" media-type=\"application/xml\"/>");
        foreach (var name in parts?.Keys.ToList() ?? [])
            items.Append($"<opf:item id=\"{Path.GetFileNameWithoutExtension(name)}\" href=\"{name}\" media-type=\"application/xml\"/>");
        if (picture is not null)
            items.Append("<opf:item id=\"image1\" href=\"BinData/image1.png\" media-type=\"image/png\" isEmbeded=\"1\"/>");
        var meta = estimatedLines ? "<opf:meta name=\"FileeLineSegments\" content=\"estimated\"/>" : "";
        var hpf = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><opf:package xmlns:opf=\"http://www.idpf.org/2007/opf/\"><opf:metadata><opf:title>t</opf:title>" + meta + "</opf:metadata>" +
                  $"<opf:manifest>{items}</opf:manifest><opf:spine><opf:itemref idref=\"header\" linear=\"yes\"/><opf:itemref idref=\"section0\" linear=\"yes\"/></opf:spine></opf:package>";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        Write("mimetype", "application/hwp+zip");
        Write("Contents/content.hpf", hpf);
        Write("Contents/header.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>" + header);
        Write("Contents/section0.xml", body);
        foreach (var (name, xml) in parts ?? [])
            Write(name, xml);
        if (picture is not null)
        {
            using var stream = zip.CreateEntry("BinData/image1.png").Open();
            stream.Write(picture);
        }
        return path;
    }

    /// <summary>borderFill 1 (no lines), charPr 0 and paraPr 0 (160%), plus the given items.</summary>
    private static string RefList(string borderFills = "", string charPrs = "", string paraPrs = "") =>
        $"<hh:borderFills>{BorderFill("1", "type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"")}{borderFills}</hh:borderFills>" +
        $"<hh:charProperties>{CharPr("0")}{charPrs}</hh:charProperties>" +
        "<hh:tabProperties><hh:tabPr id=\"0\" autoTabLeft=\"0\" autoTabRight=\"0\"/></hh:tabProperties>" +
        $"<hh:paraProperties>{ParaPr("0", LineSpacing("PERCENT", 160))}{paraPrs}</hh:paraProperties>";

    private HDocument ReadHwpx(string path, bool forLayout = false, Dictionary<string, int>? skipped = null)
    {
        var media = Path.Combine(Path.GetDirectoryName(path)!, "media");
        return forLayout ? HwpxReader.ReadForLayout(path, media, skipped) : HwpxReader.Read(path, media, skipped);
    }

    private static List<HParagraph> Paragraphs(HDocument document) => document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();

    // ───────────────────────── HWPX: characters ─────────────────────────

    [Fact]
    public void Hwpx_character_shapes_keep_all_seven_language_slots()
    {
        var path = Package(Path.Combine(fx.NewFolder(), "slots.hwpx"),
            RefList(charPrs: CharPr("1", fontRef: "1,0,1,0,1,0,1", ratio: "90,80,100,100,100,100,100", spacing: "-5,10,0,0,0,0,0", relSz: "100,95,100,100,100,100,100", offset: "0,-10,0,0,0,0,0")),
            Hp(Hr(Ht("한글 Latin"), "1")));

        var text = Assert.IsType<HText>(Paragraphs(ReadHwpx(path))[1].Inlines.Single());

        // What the writers use stays as before: the Latin and Hangul faces, the Hangul spacing.
        Assert.Equal(("LATIN-0", "HANGUL-1", -5), (text.Format.Font, text.Format.EastAsianFont, text.Format.Spacing));
        var slots = text.Format.Layout!.Slots!;
        Assert.Equal(new HSlotFormat("HANGUL-1", 90, -5, 100, 0), slots.Hangul);
        Assert.Equal(new HSlotFormat("LATIN-0", 80, 10, 95, -10), slots.Latin);
        Assert.Equal(["HANGUL-1", "LATIN-0", "HANJA-1", "JAPANESE-0", "OTHER-1", "SYMBOL-0", "USER-1"], HLanguageSlots.Languages.Select(l => slots[l].Font));
    }

    [Fact]
    public void Runs_that_differ_only_in_layout_detail_stay_apart_when_read_for_layout()
    {
        var path = Package(Path.Combine(fx.NewFolder(), "runs.hwpx"),
            RefList(charPrs: CharPr("1", ratio: "80")),
            Hp(Hr(Ht("보통")) + Hr(Ht("좁게"), "1")));

        // For the writers the runs look alike and are one text, as before; the layout engine sees both.
        var merged = Paragraphs(ReadHwpx(path))[1].Inlines;
        Assert.Equal("보통좁게", Assert.IsType<HText>(Assert.Single(merged)).Text);
        var apart = Paragraphs(ReadHwpx(path, forLayout: true))[1].Inlines.Cast<HText>().ToList();
        Assert.Equal([("보통", 100), ("좁게", 80)], apart.Select(t => (t.Text, t.Format.Layout!.Slots!.Hangul.Ratio!.Value)));
        Assert.Equal(apart[0].Format, apart[1].Format);
        Assert.False(apart[0].Format.SameLayout(apart[1].Format));
    }

    // ───────────────────────── HWPX: paragraphs ─────────────────────────

    [Fact]
    public void Hwpx_paragraph_shapes_keep_line_rules_borders_and_line_breaking()
    {
        var path = Package(Path.Combine(fx.NewFolder(), "paragraphs.hwpx"),
            RefList(
                borderFills: BorderFill("2", "type=\"SOLID\" width=\"0.4 mm\" color=\"#0000FF\"", "<hc:fillBrush><hc:winBrush faceColor=\"#FFF2CC\" hatchColor=\"#000000\" alpha=\"0\"/></hc:fillBrush>"),
                paraPrs:
                    ParaPr("1", LineSpacing("BETWEEN_LINES", 500), "condense=\"25\" fontLineHeight=\"1\" snapToGrid=\"0\"",
                        "breakLatinWord=\"HYPHENATION\" breakNonLatinWord=\"BREAK_WORD\" lineWrap=\"SQUEEZE\"", "eAsianEng=\"1\" eAsianNum=\"0\"",
                        "borderFillIDRef=\"2\" offsetLeft=\"100\" offsetRight=\"200\" offsetTop=\"300\" offsetBottom=\"400\" connect=\"1\" ignoreMargin=\"1\"") +
                    ParaPr("2", LineSpacing("FIXED", 1800))),
            Hp(Hr(Ht("상자")), "1") + Hp(Hr(Ht("고정")), "2"));

        var paragraphs = Paragraphs(ReadHwpx(path));
        var boxed = paragraphs[1].Format;
        // The writers' line spacing is unchanged; the rule keeps 한글's meaning ("space between lines only").
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 1500), boxed.LineSpacing);
        var layout = boxed.Layout!;
        Assert.Equal(new HLineHeight(HLineHeightRule.BetweenLines, 500), layout.LineHeight);
        Assert.Equal((false, true), (layout.SnapToGrid, layout.FontLineHeight));
        // breakNonLatinWord BREAK_WORD is 한글's 어절 (whole words): 한글 names the two the other way round.
        Assert.Equal(new HLineBreaking(HLatinBreak.Hyphenation, HNonLatinBreak.KeepWord, HLineWrap.Squeeze, AutoSpaceLatin: true, AutoSpaceDigits: false, Condense: 25), layout.Breaking);
        var border = layout.Border!;
        Assert.Equal(new HBorder(HBorderStyle.Solid, 0.4, "#0000FF"), border.Borders.Top);
        Assert.Equal(("#FFF2CC", new HInsets(100, 200, 300, 400), true, true), (border.Fill, border.Offsets, border.Connect, border.IgnoreMargin));
        Assert.True(border.IsVisible);

        Assert.Equal(new HLineHeight(HLineHeightRule.Exact, 1800), paragraphs[2].Format.Layout!.LineHeight);
        Assert.False(paragraphs[2].Format.Layout!.Border!.IsVisible);
        Assert.Equal(new HLineHeight(HLineHeightRule.Percent, 160), paragraphs[0].Format.Layout!.LineHeight);
    }

    [Fact]
    public void Hwpx_stored_line_layout_is_kept_and_marked_when_Filee_estimated_it()
    {
        const string Lines =
            "<hp:linesegarray><hp:lineseg textpos=\"0\" vertpos=\"0\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"393216\"/>" +
            "<hp:lineseg textpos=\"12\" vertpos=\"1600\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"100\" horzsize=\"42420\" flags=\"0\"/></hp:linesegarray>";
        var dir = fx.NewFolder();
        var section = Hp(Hr(Ht("두 줄로 나뉜 문단입니다")), lines: Lines) + Hp(Hr(Ht("저장된 줄 없음")));

        var stored = Paragraphs(ReadHwpx(Package(Path.Combine(dir, "stored.hwpx"), RefList(), section)));
        var lines = stored[1].Lines!;
        Assert.False(lines.Estimated);
        Assert.Equal([new HLineSegment(0, 0, 1000, 1000, 850, 600, 0, 42520, 393216), new HLineSegment(12, 1600, 1000, 1000, 850, 600, 100, 42420, 0)], lines.Segments);
        Assert.Null(stored[2].Lines);

        var estimated = Paragraphs(ReadHwpx(Package(Path.Combine(dir, "estimated.hwpx"), RefList(), section, estimatedLines: true)));
        Assert.True(estimated[1].Lines!.Estimated);
    }

    [Fact]
    public void Hwpx_stored_line_starts_convert_from_hangul_positions_to_the_text()
    {
        // 한글 counts every control as eight positions (a tab, a footnote, an equation) and a line break as one; the
        // model's text has one character for a tab and a line break, none for a note, and an equation's script.
        static string Seg(int position) =>
            $"<hp:lineseg textpos=\"{position}\" vertpos=\"0\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"0\"/>";
        static string Lines(params int[] positions) => $"<hp:linesegarray>{string.Concat(positions.Select(Seg))}</hp:linesegarray>";
        var runs = Hr(Ht("가나<hp:tab width=\"4000\" leader=\"0\" type=\"1\"/>다") +
                      $"<hp:ctrl><hp:footNote number=\"1\" suffixChar=\"41\" instId=\"1\">{SubList(Hp(Hr(Ht(" 주석"))))}</hp:footNote></hp:ctrl>" +
                      Ht("라<hp:lineBreak/>마") +
                      "<hp:equation id=\"9\" zOrder=\"0\" numberingType=\"EQUATION\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" version=\"Equation Version 60\" baseLine=\"85\" textColor=\"#000000\" baseUnit=\"1000\" lineMode=\"CHAR\" font=\"HYhwpEQ\"><hp:script>x over y</hp:script></hp:equation>" +
                      Ht("바사"));
        var section = Hp(runs, lines: Lines(0, 10, 19, 21, 22, 30)) + Hp(runs, lines: Lines(0, 25)) + Hp(Hr(Ht("짧은")), lines: Lines(0, 2));

        var paragraphs = Paragraphs(ReadHwpx(Package(Path.Combine(fx.NewFolder(), "starts.hwpx"), RefList(), section)));

        Assert.Equal("가나\t다라" + ParagraphText.LineSeparator + "마x over y바사", ParagraphText.From(paragraphs[1]).Text);
        Assert.Equal([0, 3, 4, 6, 7, 15], paragraphs[1].Lines!.Starts!);
        Assert.Null(paragraphs[2].Lines!.Starts); // a line can't start inside the equation
        Assert.Equal([0, 2], paragraphs[3].Lines!.Starts!); // the end of the text
    }

    [Fact]
    public void Hwpx_stored_line_starts_are_dropped_when_a_table_splits_the_paragraph()
    {
        var table =
            "<hp:tbl id=\"20\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"0\" rowCnt=\"1\" colCnt=\"1\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"6000\" widthRelTo=\"ABSOLUTE\" height=\"1000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + FloatingPos + "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>" +
            "<hp:tr><hp:tc name=\"\" header=\"0\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"1\">" + SubList(Hp(Hr(Ht("칸")))) +
            "<hp:cellAddr colAddr=\"0\" rowAddr=\"0\"/><hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"6000\" height=\"1000\"/><hp:cellMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:tc></hp:tr></hp:tbl>";
        const string Lines =
            "<hp:linesegarray><hp:lineseg textpos=\"0\" vertpos=\"0\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"0\"/></hp:linesegarray>";

        var paragraphs = Paragraphs(ReadHwpx(Package(Path.Combine(fx.NewFolder(), "split.hwpx"), RefList(), Hp(Hr(Ht("앞") + table + Ht("뒤")), lines: Lines))));

        Assert.NotNull(paragraphs[1].Lines);
        Assert.Null(paragraphs[1].Lines!.Starts);
    }

    [Fact]
    public void Hwpx_table_starting_its_paragraph_keeps_the_paragraphs_stored_lines_and_its_place()
    {
        var table =
            "<hp:tbl id=\"20\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"0\" rowCnt=\"1\" colCnt=\"1\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"6000\" widthRelTo=\"ABSOLUTE\" height=\"1000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" +
            "<hp:pos treatAsChar=\"0\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"0\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"508\" horzOffset=\"559\"/>" +
            "<hp:outMargin left=\"141\" right=\"141\" top=\"142\" bottom=\"143\"/>" +
            "<hp:tr><hp:tc name=\"\" header=\"0\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"1\">" + SubList(Hp(Hr(Ht("칸")))) +
            "<hp:cellAddr colAddr=\"0\" rowAddr=\"0\"/><hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"6000\" height=\"1000\"/><hp:cellMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:tc></hp:tr></hp:tbl>";
        const string Lines =
            "<hp:linesegarray><hp:lineseg textpos=\"0\" vertpos=\"2000\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"0\"/>" +
            "<hp:lineseg textpos=\"12\" vertpos=\"3600\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"0\"/></hp:linesegarray>";

        var blocks = ReadHwpx(Package(Path.Combine(fx.NewFolder(), "opens.hwpx"), RefList(), Hp(Hr(table + Ht("뒤 문단입니다")), lines: Lines))).Sections[0].Blocks;

        // The table starts the paragraph: no part before it, its place kept, and the text after it keeps the lines,
        // which still map (the table is eight positions of 한글's and no text of the model's).
        var read = Assert.Single(blocks.OfType<HTable>());
        Assert.True(read.OpensParagraph);
        Assert.Equal((508, 559, new HInsets(141, 141, 142, 143)), (read.VerticalOffset, read.Indent, read.OuterMargin));
        var after = (HParagraph)blocks[blocks.IndexOf(read) + 1];
        Assert.Equal([0, 4], after.Lines!.Starts!);
    }

    [Fact]
    public void Hwpx_sections_keep_their_grid_and_tables_their_page_break_mode()
    {
        var table =
            "<hp:tbl id=\"20\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"NONE\" repeatHeader=\"0\" rowCnt=\"1\" colCnt=\"1\" cellSpacing=\"0\" borderFillIDRef=\"1\" noAdjust=\"0\">" +
            "<hp:sz width=\"6000\" widthRelTo=\"ABSOLUTE\" height=\"1000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" + FloatingPos + "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>" +
            "<hp:tr><hp:tc name=\"\" header=\"0\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"1\">" + SubList(Hp(Hr(Ht("칸")))) +
            "<hp:cellAddr colAddr=\"0\" rowAddr=\"0\"/><hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"6000\" height=\"1000\"/><hp:cellMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:tc></hp:tr></hp:tbl>";
        var path = Package(Path.Combine(fx.NewFolder(), "grid.hwpx"), RefList(), Hp(Hr(table)), "<hp:grid lineGrid=\"1600\" charGrid=\"0\" wonggojiFormat=\"0\"/>");

        var document = ReadHwpx(path);

        Assert.Equal(new HDocumentGrid(HGridKind.Lines, 1600), document.Sections[0].Grid);
        Assert.Equal(HTablePageBreak.None, document.Sections[0].Blocks.OfType<HTable>().Single().PageBreak);
    }

    [Fact]
    public void Hwpx_master_pages_are_read_for_layout()
    {
        var master =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><masterPage {Namespaces} id=\"masterpage0\" type=\"EVEN\" pageNumber=\"0\" pageDuplicate=\"1\" pageFront=\"0\">" +
            SubList(Hp(Hr(Ht("바탕쪽 글")))) + "</masterPage>";
        var path = Package(Path.Combine(fx.NewFolder(), "master.hwpx"), RefList(), Hp(Hr(Ht("본문"))),
            "<hp:masterPage idRef=\"masterpage0\"/>", new() { ["Contents/masterpage0.xml"] = master });

        Assert.Empty(ReadHwpx(path).Sections[0].MasterPages);
        var page = Assert.Single(ReadHwpx(path, forLayout: true).Sections[0].MasterPages);
        Assert.Equal((HMasterPageKind.Even, true, false), (page.Kind, page.Duplicate, page.Front));
        Assert.Equal("바탕쪽 글", Assert.IsType<HText>(Assert.IsType<HParagraph>(Assert.Single(page.Blocks)).Inlines.Single()).Text);
    }

    // ───────────────────────── HWPX: objects ─────────────────────────

    [Fact]
    public void Hwpx_pictures_keep_crop_rotation_mirroring_and_z_order()
    {
        using var image = new MagickImage(MagickColors.Teal, 40, 20);
        var picture =
            "<hp:pic id=\"5\" zOrder=\"7\" numberingType=\"PICTURE\" textWrap=\"SQUARE\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"6\" reverse=\"0\">" +
            "<hp:offset x=\"0\" y=\"0\"/><hp:orgSz width=\"3000\" height=\"1500\"/><hp:curSz width=\"3000\" height=\"1500\"/><hp:flip horizontal=\"1\" vertical=\"0\"/>" +
            "<hp:rotationInfo angle=\"30\" centerX=\"1500\" centerY=\"750\" rotateimage=\"1\"/>" +
            "<hp:imgRect><hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"3000\" y=\"0\"/><hc:pt2 x=\"3000\" y=\"1500\"/><hc:pt3 x=\"0\" y=\"1500\"/></hp:imgRect>" +
            "<hp:imgClip left=\"300\" right=\"2700\" top=\"0\" bottom=\"1200\"/><hp:inMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:imgDim dimwidth=\"3000\" dimheight=\"1500\"/>" +
            "<hc:img binaryItemIDRef=\"image1\" bright=\"0\" contrast=\"0\" effect=\"REAL_PIC\" alpha=\"0\"/><hp:effects/>" +
            $"<hp:sz width=\"3000\" widthRelTo=\"ABSOLUTE\" height=\"1500\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>{FloatingPos}<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:pic>";
        var path = Package(Path.Combine(fx.NewFolder(), "picture.hwpx"), RefList(), Hp(Hr(picture)), picture: image.ToByteArray(MagickFormat.Png));

        var read = Assert.IsType<HImage>(Paragraphs(ReadHwpx(path))[1].Inlines.Single());

        Assert.Equal(new HCrop(0.1, 0, 0.1, 0.2), read.Crop);
        Assert.Equal(new HTransform(30, FlipHorizontal: true), read.Transform);
        Assert.Equal(7, read.ZOrder);
    }

    [Fact]
    public void Hwpx_polygons_curves_and_arcs_are_shapes_when_read_for_layout()
    {
        var polygon = Drawing("polygon", "", "<hc:pt x=\"0\" y=\"0\"/><hc:pt x=\"4000\" y=\"0\"/><hc:pt x=\"2000\" y=\"2000\"/><hc:pt x=\"0\" y=\"0\"/>", zOrder: 3);
        var curve = Drawing("curve", "", "<hp:seg type=\"CURVE\" x1=\"0\" y1=\"2000\" x2=\"1000\" y2=\"0\"/><hp:seg type=\"LINE\" x1=\"1000\" y1=\"0\" x2=\"4000\" y2=\"2000\"/>",
            "<hp:flip horizontal=\"0\" vertical=\"1\"/><hp:rotationInfo angle=\"90\" centerX=\"0\" centerY=\"0\" rotateimage=\"1\"/>");
        var arc = Drawing("arc", "type=\"PIE\"", "<hc:center x=\"2000\" y=\"1000\"/><hc:ax1 x=\"4000\" y=\"1000\"/><hc:ax2 x=\"2000\" y=\"0\"/>");
        var labelled = Drawing("polygon", "", "<hp:drawText lastWidth=\"4000\" name=\"\" editable=\"0\">" + SubList(Hp(Hr(Ht("다각형 글")))) +
            "<hp:textMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/></hp:drawText><hc:pt x=\"0\" y=\"0\"/><hc:pt x=\"4000\" y=\"2000\"/><hc:pt x=\"0\" y=\"2000\"/>");
        var path = Package(Path.Combine(fx.NewFolder(), "shapes.hwpx"), RefList(), Hp(Hr(polygon + curve + arc + labelled)));

        // For the writers nothing changes: shapes they cannot draw are skipped, text keeps a box without outline.
        var skipped = new Dictionary<string, int>();
        var plain = Paragraphs(ReadHwpx(path, skipped: skipped))[1].Inlines;
        Assert.Equal((1, 1, 1), (skipped["polygon"], skipped["curve"], skipped["arc"]));
        Assert.Equal(HBorderStyle.None, Assert.IsType<HTextBox>(Assert.Single(plain)).Line.Style);

        var inlines = Paragraphs(ReadHwpx(path, forLayout: true))[1].Inlines;
        var shape = Assert.IsType<HShape>(inlines[0]);
        Assert.Equal((HShapeKind.Polygon, 8000, 4000, 3, "#00FF00", "#FF0000"), (shape.Kind, shape.Width, shape.Height, shape.ZOrder, shape.Fill, shape.Line.Color));
        Assert.Equal((4000, 2000), (shape.Geometry!.Width, shape.Geometry.Height));
        Assert.Equal([new HPoint(0, 0), new HPoint(4000, 0), new HPoint(2000, 2000), new HPoint(0, 0)], shape.Geometry.Points!);

        var curved = Assert.IsType<HShape>(inlines[1]);
        Assert.Equal(HShapeKind.Curve, curved.Kind);
        Assert.Equal([new HCurveSegment(true, new HPoint(0, 2000), new HPoint(1000, 0)), new HCurveSegment(false, new HPoint(1000, 0), new HPoint(4000, 2000))], curved.Geometry!.Segments!);
        Assert.Equal(new HTransform(90, FlipVertical: true), curved.Transform);

        var pie = Assert.IsType<HShape>(inlines[2]);
        Assert.Equal(new HArc(HArcKind.Pie, new HPoint(2000, 1000), new HPoint(4000, 1000), new HPoint(2000, 0)), pie.Geometry!.Arc);

        var box = Assert.IsType<HTextBox>(inlines[3]);
        Assert.Equal((HShapeKind.Polygon, HBorderStyle.Solid, "#00FF00", 3), (box.Shape, box.Line.Style, box.Fill, box.Geometry!.Points!.Count));
    }

    [Fact]
    public void Hwpx_rectangles_keep_rotation_and_z_order()
    {
        var rect = Drawing("rect", "ratio=\"0\"", "<hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"4000\" y=\"0\"/><hc:pt2 x=\"4000\" y=\"2000\"/><hc:pt3 x=\"0\" y=\"2000\"/>",
            "<hp:flip horizontal=\"1\" vertical=\"0\"/><hp:rotationInfo angle=\"45\" centerX=\"0\" centerY=\"0\" rotateimage=\"1\"/>", zOrder: 9);
        var path = Package(Path.Combine(fx.NewFolder(), "rect.hwpx"), RefList(), Hp(Hr(rect)));

        var shape = Assert.IsType<HShape>(Paragraphs(ReadHwpx(path))[1].Inlines.Single());

        Assert.Equal((new HTransform(45, FlipHorizontal: true), 9), (shape.Transform, shape.ZOrder));
        // The mirroring the writers use is only for lines, whose direction it is.
        Assert.False(shape.FlipHorizontal);
    }

    // ───────────────────────── DOCX ─────────────────────────

    private HDocument ReadDocx(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "test.docx")), Path.Combine(dir, "media"));
    }

    [Fact]
    public void Docx_line_rules_keep_their_meaning_and_the_document_grid()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("배수"), "<w:spacing w:line=\"276\" w:lineRule=\"auto\"/>"))
            .Paragraph(P(R("고정"), "<w:spacing w:line=\"360\" w:lineRule=\"exact\"/>"))
            .Paragraph(P(R("최소"), "<w:spacing w:line=\"300\" w:lineRule=\"atLeast\"/>"))
            .Paragraph(P(R("격자"), "<w:spacing w:line=\"240\" w:lineRule=\"auto\"/>"))
            .Paragraph(P(R("격자 무시"), "<w:snapToGrid w:val=\"0\"/>"));
        docx.SectionProperties = DefaultPage + "<w:docGrid w:type=\"lines\" w:linePitch=\"360\" w:charSpace=\"-1024\"/>";

        var document = ReadDocx(docx);
        var paragraphs = Paragraphs(document);

        // 1.15 lines on an 18 pt grid: the writers still get the snapped "at least" height, the rule stays Word's multiple with its grid.
        Assert.Equal(new HDocumentGrid(HGridKind.Lines, 1800, CharSpace: -1024), document.Sections[0].Grid);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 1800 * 276 / 240), paragraphs[0].Format.LineSpacing);
        Assert.Equal(new HLineHeight(HLineHeightRule.Multiple, 276, GridPitch: 1800), paragraphs[0].Format.Layout!.LineHeight);
        Assert.Equal(new HLineHeight(HLineHeightRule.Exact, 1800), paragraphs[1].Format.Layout!.LineHeight);
        Assert.Equal(new HLineHeight(HLineHeightRule.AtLeast, 1500), paragraphs[2].Format.Layout!.LineHeight);
        Assert.Equal(new HLineHeight(HLineHeightRule.Multiple, 240, GridPitch: 1800), paragraphs[3].Format.Layout!.LineHeight);
        Assert.Equal((new HLineHeight(HLineHeightRule.Multiple, 240), false), (paragraphs[4].Format.Layout!.LineHeight, paragraphs[4].Format.Layout!.SnapToGrid));
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 130), paragraphs[4].Format.LineSpacing);
    }

    [Fact]
    public void Docx_runs_keep_their_four_fonts_scale_spacing_and_position()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("글자", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Calibri\" w:eastAsia=\"바탕\" w:cs=\"Arial Unicode MS\" w:hint=\"eastAsia\"/><w:w w:val=\"80\"/><w:spacing w:val=\"40\"/><w:position w:val=\"6\"/>")))
            .Paragraph(P(R("테마", "<w:rFonts w:cstheme=\"minorBidi\"/>")));

        var paragraphs = Paragraphs(ReadDocx(docx));

        var format = Assert.IsType<HText>(paragraphs[0].Inlines.Single()).Format;
        Assert.Equal(("Arial", "바탕"), (format.Font, format.EastAsianFont)); // what the writers use
        Assert.Equal(new HCharLayout(WordFonts: new HWordFonts("Arial", "Calibri", "바탕", "Arial Unicode MS", "eastAsia"), Scale: 80, LetterSpacing: 200, Position: 300), format.Layout);
        // Without a theme part the complex script theme font is unknown; the other fonts come from the defaults.
        Assert.Equal(new HWordFonts("맑은 고딕", "맑은 고딕", "맑은 고딕", null), Assert.IsType<HText>(paragraphs[1].Inlines.Single()).Format.Layout!.WordFonts);
    }

    [Fact]
    public void Docx_paragraphs_keep_borders_shading_and_line_breaking()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("테두리"),
            "<w:pBdr><w:top w:val=\"single\" w:sz=\"8\" w:space=\"4\" w:color=\"FF0000\"/><w:between w:val=\"dashed\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/></w:pBdr>" +
            "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"DDEEFF\"/><w:kinsoku w:val=\"0\"/><w:wordWrap w:val=\"0\"/><w:overflowPunct w:val=\"0\"/><w:topLinePunct/>" +
            "<w:autoSpaceDE w:val=\"0\"/><w:autoSpaceDN w:val=\"0\"/>"))
            .Paragraph(P(R("기본")));

        var paragraphs = Paragraphs(ReadDocx(docx));

        var border = paragraphs[0].Format.Layout!.Border!;
        Assert.Equal(new HBorder(HBorderStyle.Solid, 8 / 8.0 * 25.4 / 72, "#FF0000"), border.Borders.Top);
        Assert.Equal(HBorderStyle.None, border.Borders.Bottom.Style);
        Assert.Equal((HBorderStyle.Dash, "#DDEEFF", 400, true), (border.Between!.Value.Style, border.Fill, border.Offsets.Top, border.Connect));
        Assert.Equal(new HLineBreaking(HLatinBreak.BreakWord, Kinsoku: false, OverflowPunctuation: false, CompressPunctuation: true, AutoSpaceLatin: false, AutoSpaceDigits: false),
            paragraphs[0].Format.Layout!.Breaking);

        // Word's defaults when nothing is said.
        Assert.Equal(HParaBorder.None, paragraphs[1].Format.Layout!.Border);
        Assert.Equal(new HLineBreaking(HLatinBreak.KeepWord, Kinsoku: true, OverflowPunctuation: true, CompressPunctuation: false, AutoSpaceLatin: true, AutoSpaceDigits: true),
            paragraphs[1].Format.Layout!.Breaking);
    }

    [Fact]
    public void Docx_objects_keep_crop_rotation_mirroring_and_z_order_and_rows_their_split_setting()
    {
        var docx = new DocxBuilder { Settings = "<w:noLineBreaksAfter w:lang=\"ja-JP\" w:val=\"(「\"/><w:noLineBreaksBefore w:lang=\"ja-JP\" w:val=\")」。\"/>" };
        using var image = new MagickImage(MagickColors.Orange, 40, 20);
        var id = docx.Image(image.ToByteArray(MagickFormat.Png));
        var picture = Picture(id, 914400, 457200)
            .Replace("</pic:blipFill>", "<a:srcRect l=\"10000\" t=\"0\" r=\"25000\" b=\"5000\"/></pic:blipFill>", StringComparison.Ordinal)
            .Replace("<a:xfrm>", "<a:xfrm rot=\"5400000\" flipH=\"1\">", StringComparison.Ordinal);
        var position = "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>";
        var shape = "<wps:wsp><wps:spPr><a:xfrm rot=\"-1800000\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"/></wps:spPr><wps:bodyPr/></wps:wsp>";
        docx.Paragraph(P(Anchor(PictureUri, picture, 914400, 457200, position[..position.IndexOf("<wp:positionV", StringComparison.Ordinal)], position[position.IndexOf("<wp:positionV", StringComparison.Ordinal)..])
                         .Replace("relativeHeight=\"1\"", "relativeHeight=\"251660288\"", StringComparison.Ordinal)))
            .Paragraph(P(Anchor(ShapeUri, shape, 914400, 457200, position[..position.IndexOf("<wp:positionV", StringComparison.Ordinal)], position[position.IndexOf("<wp:positionV", StringComparison.Ordinal)..])))
            .Paragraph("<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:trPr><w:cantSplit/></w:trPr><w:tc>" + P(R("칸")) + "</w:tc></w:tr><w:tr><w:tc>" + P(R("칸")) + "</w:tc></w:tr></w:tbl>");

        var document = ReadDocx(docx);
        var inlines = Paragraphs(document).SelectMany(p => p.Inlines).ToList();

        var read = inlines.OfType<HImage>().Single();
        Assert.Equal(new HCrop(0.1, 0, 0.25, 0.05), read.Crop);
        Assert.Equal((new HTransform(90, FlipHorizontal: true), 251660288), (read.Transform, read.ZOrder));
        Assert.Equal(new HTransform(-30), inlines.OfType<HShape>().Single().Transform);
        Assert.Equal([true, false], document.Sections[0].Blocks.OfType<HTable>().Single().Rows.Select(r => r.CantSplit));
        Assert.Equal(new HKinsoku("ja-JP", ")」。", "(「"), Assert.Single(document.Kinsoku));
    }

    // ───────────────────────── Writers ─────────────────────────

    /// <summary>The parts of a written package (pictures by name only), to compare two writes.</summary>
    private static Dictionary<string, string> Parts(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return e.FullName.StartsWith("BinData/", StringComparison.Ordinal) || e.FullName.StartsWith("word/media/", StringComparison.Ordinal) ? "" : reader.ReadToEnd();
        });
    }

    [Fact]
    public void Layout_detail_does_not_change_written_files()
    {
        HDocument Build(bool detail)
        {
            var slots = HLanguageSlots.All(new HSlotFormat("궁서", 80, 5, 90, 10));
            HCharFormat Char(HCharFormat format) => detail
                ? format with { Layout = new HCharLayout(slots, new HWordFonts("A", "B", "C", "D"), 90, 100, 50) }
                : format;
            var paraLayout = new HParaLayout(new HLineHeight(HLineHeightRule.Multiple, 276, 1800),
                new HParaBorder(HBorders.Grid, "#FFFF00", new HInsets(1, 2, 3, 4), true), new HLineBreaking(HLatinBreak.BreakWord, Kinsoku: false), true, true);

            var document = new HDocument { Title = "제목" };
            var section = new HSection();
            if (detail)
            {
                section.Grid = new HDocumentGrid(HGridKind.Lines, 1800);
                var master = new HMasterPage(HMasterPageKind.Both);
                master.Blocks.Add(new HParagraph());
                section.MasterPages.Add(master);
                document.Kinsoku.Add(new HKinsoku(null, ")", "("));
            }
            document.Sections.Add(section);
            foreach (var bold in new[] { false, true, false })
            {
                var paragraph = new HParagraph
                {
                    Format = new HParaFormat(Align: HAlign.Justify, LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 150), Layout: detail ? paraLayout : null),
                    MarkFormat = Char(new HCharFormat(Size: 1100)),
                    Lines = detail ? new HLineLayout([new HLineSegment(0, 0, 1000, 1000, 850, 600, 0, 42520, 0)], Estimated: true) : null,
                };
                paragraph.Inlines.Add(new HText(bold ? "굵게" : "보통", Char(new HCharFormat(Bold: bold, Size: 1100, Font: "Arial"))));
                paragraph.Inlines.Add(new HText(" 이어서", Char(new HCharFormat(Size: 1100))));
                paragraph.Inlines.Add(new HShape(HShapeKind.Rectangle)
                {
                    Width = 2000,
                    Height = 1000,
                    Anchor = new HAnchor("COLUMN", "LEFT", 0, "PARA", "TOP", 0, "SQUARE", true),
                    Transform = detail ? new HTransform(30, true) : null,
                    ZOrder = detail ? 5 : null,
                });
                section.Blocks.Add(paragraph);
            }
            var table = new HTable { ColumnCount = 1, PageBreak = detail ? HTablePageBreak.None : null };
            var row = new HRow { CantSplit = detail };
            var cell = new HCell();
            cell.Blocks.Add(new HParagraph { Format = new HParaFormat(Layout: detail ? paraLayout : null) });
            row.Cells.Add(cell);
            table.Rows.Add(row);
            section.Blocks.Add(table);
            return document;
        }

        var dir = fx.NewFolder();
        string Write(string name, Action<HDocument, string> writer, bool detail)
        {
            var path = Path.Combine(dir, name);
            writer(Build(detail), path);
            return path;
        }
        Assert.Equal(Parts(Write("plain.hwpx", (d, p) => HwpxWriter.Write(d, p), false)), Parts(Write("detail.hwpx", (d, p) => HwpxWriter.Write(d, p), true)));
        Assert.Equal(Parts(Write("plain.docx", DocxWriter.Write, false)), Parts(Write("detail.docx", DocxWriter.Write, true)));
        Assert.Equal(PlainTextWriter.Write(Build(false)), PlainTextWriter.Write(Build(true)));
        Assert.Equal(MarkdownWriter.Write(Build(false), _ => null), MarkdownWriter.Write(Build(true), _ => null));
    }

    [Fact]
    public void Formats_compare_without_layout_detail_unless_asked()
    {
        var plain = new HCharFormat(Bold: true, Size: 1000);
        var detailed = plain with { Layout = new HCharLayout(Scale: 80) };
        Assert.Equal(plain, detailed);
        Assert.Equal(plain.GetHashCode(), detailed.GetHashCode());
        Assert.Equal(plain.ToString(), detailed.ToString());
        Assert.True(detailed.IsEmpty == plain.IsEmpty && new HCharFormat(Layout: new HCharLayout(Scale: 80)).IsEmpty);
        Assert.False(plain.SameLayout(detailed));
        Assert.True(detailed.SameLayout(detailed with { Layout = new HCharLayout(Scale: 80) }));

        var paragraph = new HParaFormat(Align: HAlign.Center);
        var withRule = paragraph with { Layout = new HParaLayout(new HLineHeight(HLineHeightRule.Multiple, 240)) };
        Assert.Equal(paragraph, withRule);
        Assert.False(paragraph.SameLayout(withRule));
    }

    // ───────────────────────── Style resolver ─────────────────────────

    [Fact]
    public void Style_resolver_gives_the_values_the_written_file_has()
    {
        // A document as the Markdown and HTML readers make it: most members left to the 한글 style.
        var numbering = new HNumbering();
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false));
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        void Add(HParagraph paragraph, params HInline[] inlines)
        {
            paragraph.Inlines.AddRange(inlines);
            section.Blocks.Add(paragraph);
        }
        Add(new HParagraph(), new HText("기본", default), new HText(" 굵게", new HCharFormat(Bold: true)));
        Add(new HParagraph { HeadingLevel = 1 }, new HText("제목", default));
        Add(new HParagraph { List = new HListRef(numbering, 0, Numbered: true) }, new HText("항목", new HCharFormat(Font: "Arial", Spacing: -3)));
        Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Center, Left: 1000, FirstLine: -500, LineSpacing: new HLineSpacing(HLineSpacingKind.Fixed, 2000)) },
            new HText("가운데", new HCharFormat(Size: 1400, Color: "#123456", Superscript: true)));
        var link = new HLink("https://example.com");
        link.Content.Add(new HText("링크", default));
        Add(new HParagraph(), link);

        var path = Path.Combine(fx.NewFolder(), "resolved.hwpx");
        HwpxWriter.Write(document, path);
        var written = HwpxReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media")).Sections[0].Blocks.Cast<HParagraph>().ToList();

        var resolver = StyleResolver.FromTemplate();
        var paragraphs = section.Blocks.Cast<HParagraph>().ToList();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var resolved = resolver.Paragraph(paragraphs[i]);
            Assert.Equal(written[i].Format, resolved with { RightToLeft = null }); // HWPX has no right-to-left paragraphs
            Assert.False(resolved.RightToLeft);
            Assert.NotNull(resolved.Layout!.LineHeight);
            Assert.NotNull(resolved.Layout.Breaking);

            var source = paragraphs[i].Inlines.SelectMany(x => x is HLink l ? l.Content.Select(c => (c, true)) : [(x, false)]).ToList();
            var read = written[i].Inlines.SelectMany(x => x is HLink l ? l.Content : [x]).Cast<HText>().ToList();
            Assert.Equal(source.Count, read.Count);
            for (var j = 0; j < source.Count; j++)
            {
                var format = resolver.Character(((HText)source[j].Item1).Format, paragraphs[i], link: source[j].Item2);
                Assert.Equal(read[j].Format, format);
                Assert.True(format.Layout!.Slots!.Hangul.Font is not null && format.Layout.Slots.User.Ratio is not null);
            }
        }
        Assert.Equal("Arial", resolver.Character(new HCharFormat(Font: "Arial"), paragraphs[0]).Layout!.Slots!.Other.Font);
        Assert.Equal(-3, resolver.Character(new HCharFormat(Spacing: -3), paragraphs[0]).Layout!.Slots!.Symbol.Spacing);
    }

    [Fact]
    public void Style_resolver_keeps_the_rules_the_source_set()
    {
        var resolver = StyleResolver.FromTemplate();
        var word = new HParagraph
        {
            Format = new HParaFormat(LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 150),
                Layout: new HParaLayout(new HLineHeight(HLineHeightRule.Multiple, 276), Breaking: new HLineBreaking(Kinsoku: false))),
        };
        var resolved = resolver.Paragraph(word);
        Assert.Equal(new HLineHeight(HLineHeightRule.Multiple, 276), resolved.Layout!.LineHeight);
        Assert.False(resolved.Layout.Breaking!.Kinsoku);
        Assert.NotNull(resolved.Layout.Breaking.Latin); // the rest from the style

        var fixedOnly = new HParagraph { Format = new HParaFormat(LineSpacing: new HLineSpacing(HLineSpacingKind.Fixed, 1800)) };
        Assert.Equal(new HLineHeight(HLineHeightRule.Exact, 1800), resolver.Paragraph(fixedOnly).Layout!.LineHeight);

        var narrow = new HCharFormat(Layout: new HCharLayout(HLanguageSlots.All(new HSlotFormat(Ratio: 70))));
        var slots = resolver.Character(narrow, fixedOnly).Layout!.Slots!;
        Assert.Equal(70, slots.Latin.Ratio);
        Assert.NotNull(slots.Latin.Font); // the font from the style
    }
}
