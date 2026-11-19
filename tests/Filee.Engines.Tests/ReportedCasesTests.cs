// Documents users reported, rebuilt in code and run through Filee's own engines (the layout engine for documents, the
// slide renderer, the sheet printer, the HWP text writers), so what was fixed for them can't come back unnoticed when the
// built-in renderer becomes the default. No reporter's file is used: each case builds the structure its thread
// describes (a one-page CV, a "Page 1 of N" footer, a cropped picture, a bottom-anchored slide title ...) and checks
// the behaviour the fix gave it. One region per issue, the issue number in each test's name.
//
// Cases the renderer still gets wrong are skipped with the issue and the missing feature in the reason; un-skip them
// when that feature lands. Documents are laid out with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts), so
// every computer measures the same; without them those tests skip. Sheets print with the computer's fonts, as the sheet
// printer always does.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public sealed class ReportedCasesTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>
    /// Noto Sans for Latin, Noto Sans KR for East Asian text and Noto Sans Arabic for complex scripts, 10 pt, single
    /// spacing and no space after paragraphs. Reporters' documents used Calibri, 宋体 or Times New Roman; Noto here so
    /// every computer measures the same.
    /// </summary>
    private const string NotoStyles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Noto Sans" w:hAnsi="Noto Sans" w:eastAsia="Noto Sans KR" w:cs="Noto Sans Arabic"/>
        <w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/>
          <w:pPr><w:keepNext/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="28"/></w:rPr></w:style>
        <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblCellMar>
          <w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        """;

    /// <summary>A4 with 1 inch margins: the body's right edge, in points.</summary>
    private const float RightMargin = (11906 - 1440) / 20f;

    private const float LeftMargin = 72;

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ───────────────────────── Word: spaces and line height (#510, #556, #280) ─────────────────────────

    [Fact]
    public async Task Issue510_a_space_is_as_wide_as_the_fonts_own_space()
    {
        // rhwp drew Word's spaces about twice as wide, so lines broke early and a one-page CV ran onto two pages.
        var page = Single(await DocxAsync(Docx(P(R("aa bb cc dd ee ff")))));

        var glyphs = Glyphs(page);
        var space = glyphs.First(g => g.Char == ' ');
        var next = glyphs.Where(g => g.Y == space.Y && g.X > space.X).MinBy(g => g.X);
        Assert.Equal(Advance(RepositoryFonts.Latin, ' ', 10), next.X - space.X, 0.1f);
    }

    [Fact]
    public async Task Issue510_single_spacing_is_the_fonts_win_ascent_and_descent()
    {
        // rhwp made Word's lines about 6 % too tall; Word measures single spacing from the font's OS/2 win metrics.
        var page = Single(await DocxAsync(Docx(P(R("one")) + P(R("two")) + P(R("three")))));

        var baselines = Lines(page).Select(l => l.Y).ToList();
        Assert.Equal(3, baselines.Count);
        var expected = LineHeight(RepositoryFonts.Latin, 10);
        Assert.Equal(expected, baselines[1] - baselines[0], 0.05f);
        Assert.Equal(expected, baselines[2] - baselines[1], 0.05f);
    }

    [Fact]
    public async Task Issue510_a_one_page_cv_with_narrow_margins_stays_on_one_page()
    {
        // The CV of #510: A4 with 0.5 cm margins, filled to the last line Word fits on the page.
        const string Narrow = "<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"283\" w:right=\"283\" w:bottom=\"283\" w:left=\"283\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/>";
        var body = 16838 / 20f - 2 * 283 / 20f;
        var fits = (int)(body / LineHeight(RepositoryFonts.Latin, 10));

        string Cv(int lines) => string.Concat(Enumerable.Range(1, lines).Select(i => P(R($"Experience {i}: built document converters and kept them fast"))));
        var full = await DocxAsync(Docx(Cv(fits), Narrow));
        var over = await DocxAsync(Docx(Cv(fits + 1), Narrow));

        Assert.Single(full);
        Assert.Equal(2, over.Count);
    }

    [Fact]
    public async Task Issue510_a_heading_rule_is_drawn_under_the_heading()
    {
        // The CV's section headings carry a bottom border (w:pBdr), which rhwp draws since main #1138.
        var page = Single(await DocxAsync(Docx(
            P(R("Experience"), "<w:pStyle w:val=\"Heading1\"/><w:pBdr><w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"1\" w:color=\"000000\"/></w:pBdr>") +
            P(R("Built document converters.")))));

        var heading = Lines(page)[0].Y;
        var next = Lines(page)[1].Y;
        using var pixels = page.Render(72 * Scale);
        var rule = DarkRows(pixels, LeftMargin + 200);
        Assert.Contains(rule, y => y > heading && y < next - 8);
    }

    // ───────────────────────── Word: headers and footers (#1152, #416) ─────────────────────────

    [Fact]
    public async Task Issue1152_page_x_of_y_counts_every_page()
    {
        // NUMPAGES came out as the number Word stored when it saved (1), not the page count.
        var docx = Docx(P(R("first")) + PageBreak + P(R("second")) + PageBreak + P(R("third")));
        var footer = docx.Footer(P(R("Page ") + Field("PAGE", "1") + R(" of ") + Field("NUMPAGES", "1")));
        docx.SectionProperties = $"<w:footerReference w:type=\"default\" r:id=\"{footer}\"/>{DefaultPage}";

        var pages = await DocxAsync(docx);

        Assert.Equal(3, pages.Count);
        for (var i = 0; i < 3; i++)
            Assert.Contains($"Page{i + 1}of3", Text(pages[i]));
    }

    [Fact]
    public async Task Issue1152_a_first_page_header_with_content_shows_on_page_one()
    {
        var docx = Docx(P(R("cover")) + PageBreak + P(R("contents")));
        var first = docx.Header(P(R("Cover header")));
        var other = docx.Header(P(R("Running header")));
        docx.SectionProperties = $"<w:headerReference w:type=\"first\" r:id=\"{first}\"/><w:headerReference w:type=\"default\" r:id=\"{other}\"/>{DefaultPage}<w:titlePg/>";

        var pages = await DocxAsync(docx);

        Assert.Contains("Coverheader", Text(pages[0]));
        Assert.DoesNotContain("Runningheader", Text(pages[0]));
        Assert.Contains("Runningheader", Text(pages[1]));
    }

    [Fact]
    public async Task Issue1152_page_x_of_y_in_a_first_page_header_table_counts_every_page()
    {
        // The Doc Ref table of a first-page header: "Page 1 of 2" where Word last saved two pages, in a three-page
        // document. The HWPX writer keeps Word's text there (rhwp leaves such a number blank); the renderer counts.
        var docx = Docx(P(R("first")) + PageBreak + P(R("second")) + PageBreak + P(R("third")));
        var cell = P(R("Page ") + Field("PAGE", "1") + R(" of ") + Field("NUMPAGES", "2"));
        var first = docx.Header("<w:tbl><w:tblPr><w:tblW w:w=\"4000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid>" +
            $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4000\" w:type=\"dxa\"/></w:tcPr>{cell}</w:tc></w:tr></w:tbl>" + P(""));
        docx.SectionProperties = $"<w:headerReference w:type=\"first\" r:id=\"{first}\"/>{DefaultPage}<w:titlePg/>";
        var path = docx.Save(Path.Combine(fx.NewFolder(), "case.docx"));

        var pages = await ComposeAsync(path, "docx");

        Assert.Equal(3, pages.Count);
        Assert.Contains("Page1of3", Text(pages[0]));
        var document = DocxReader.Read(path, fx.NewFolder());
        var header = Assert.IsType<List<HBlock>>(Assert.Single(document.Sections).FirstPageHeader);
        var fields = Assert.IsType<HTable>(header[0]).Rows[0].Cells[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HField>().ToList();
        Assert.Equal(["1", "2"], fields.Select(f => f.Stored));
        var hwpx = Path.Combine(fx.NewFolder(), "case.hwpx");
        HwpxWriter.Write(document, hwpx);
        using var zip = ZipFile.OpenRead(hwpx);
        using var section = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
        Assert.Contains("Page 1 of 2", await section.ReadToEndAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issue1152_table_of_contents_entries_are_not_drawn_as_blue_links()
    {
        // Word writes each entry of a table of contents as a link in the Hyperlink character style (blue, underlined),
        // yet draws it in the TOC style's black, as LibreOffice does; a link in the text stays blue.
        const string Hyperlink = "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0000FF\"/><w:u w:val=\"single\"/></w:rPr></w:style>";
        static string Entry(string text, string anchor) =>
            $"<w:hyperlink w:anchor=\"{anchor}\" w:history=\"1\">{R(text, "<w:rStyle w:val=\"Hyperlink\"/><w:noProof/>")}</w:hyperlink>";
        var toc = P("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-3\" \\h \\z \\u </w:instrText></w:r>" +
                    "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + Entry("Introduction", "_Toc1")) +
                  P(Entry("Results", "_Toc2") + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");
        var docx = Docx(toc + P(Entry("Elsewhere", "_Toc1")));
        docx.Styles = NotoStyles + Hyperlink;

        var page = Single(await DocxAsync(docx));

        SKColor ColorOf(string word) => Glyphs(page).First(g => g.Char == word[0] && Lines(page).Any(l => l.Text == word && MathF.Round(g.Y, 1) == l.Y)).Run.Color;
        Assert.Equal(SKColors.Black, ColorOf("Introduction"));
        Assert.Equal(SKColors.Black, ColorOf("Results"));
        Assert.Equal(new SKColor(0, 0, 0xFF), ColorOf("Elsewhere"));
    }

    [Fact]
    public async Task Issue1152_a_header_taller_than_the_top_margin_moves_the_body_down()
    {
        // Eight header lines from 0.5 in: the header ends near 155 pt, well under the 1 inch (72 pt) top margin.
        var docx = Docx(P(R("Body starts here")));
        var header = docx.Header(string.Concat(Enumerable.Range(1, 8).Select(i => P(R($"Letterhead line {i}")))));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/>{DefaultPage}";

        var page = Single(await DocxAsync(docx));

        var lines = Lines(page);
        var headerEnd = lines.Single(l => l.Text.Contains("line8", StringComparison.Ordinal)).Y;
        var body = lines.Single(l => l.Text.Contains("Bodystarts", StringComparison.Ordinal)).Y;
        Assert.True(body > headerEnd + 5, $"body baseline {body}, header's last baseline {headerEnd}");
    }

    [Fact]
    public async Task Issue1152_a_continuous_sections_header_starts_on_the_next_page()
    {
        var docx = Docx("");
        var a = docx.Header(P(R("Header of part one")));
        var b = docx.Header(P(R("Header of part two")));
        docx.Paragraph(P(R("Part one"), $"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"{a}\"/>{DefaultPage}</w:sectPr>"));
        docx.Paragraph(P(R("Part two starts on the same page")) + PageBreak + P(R("Part two goes on")));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{b}\"/><w:type w:val=\"continuous\"/>{DefaultPage}";

        var pages = await DocxAsync(docx);

        Assert.Equal(2, pages.Count);
        Assert.Contains("Headerofpartone", Text(pages[0]));
        Assert.DoesNotContain("Headerofparttwo", Text(pages[0]));
        Assert.Contains("Headerofparttwo", Text(pages[1]));
    }

    [Fact]
    public async Task Issue1152_a_table_styles_paragraph_spacing_sets_its_row_heights()
    {
        // Word's defaults (10 pt after, 1.15 lines, in w:docDefaults as Word writes them) under a table style that sets
        // single spacing and no space after: the rows are one line high (main #1394). A paragraph style that sets its
        // own spacing would win over the table style.
        const string Compact = """
            <w:style w:type="table" w:styleId="Compact"><w:name w:val="Compact"/><w:basedOn w:val="TableNormal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:style>
            """;
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i => $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4000\" w:type=\"dxa\"/></w:tcPr>{P(R($"Row {i}"))}</w:tc></w:tr>"));
        var docx = Docx($"<w:tbl><w:tblPr><w:tblStyle w:val=\"Compact\"/><w:tblW w:w=\"4000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid>{rows}</w:tbl>" + P(R("after")));
        var styles = NotoStyles.Replace("<w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>",
            "<w:spacing w:after=\"200\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>", StringComparison.Ordinal);
        Assert.NotEqual(NotoStyles, styles);
        docx.Styles = styles + Compact;

        var page = Single(await DocxAsync(docx));

        var baselines = Lines(page).Where(l => l.Text.StartsWith("Row", StringComparison.Ordinal)).Select(l => l.Y).ToList();
        Assert.Equal(5, baselines.Count);
        var single = LineHeight(RepositoryFonts.Latin, 10);
        Assert.All(baselines.Zip(baselines.Skip(1), (a, b) => b - a), pitch => Assert.InRange(pitch, single - 0.5f, single + 2));
    }

    [Fact]
    public async Task Issue1152_the_space_after_a_cells_paragraph_counts_in_its_row()
    {
        // The same table without a table style: Normal's 1.15 lines and 10 pt after reach every cell.
        var rows = string.Concat(Enumerable.Range(1, 3).Select(i => $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4000\" w:type=\"dxa\"/></w:tcPr>{P(R($"Row {i}"))}</w:tc></w:tr>"));
        var docx = Docx($"<w:tbl><w:tblPr><w:tblW w:w=\"4000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid>{rows}</w:tbl>");
        docx.Styles = NotoStyles.Replace("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>",
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"200\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:style>", StringComparison.Ordinal);

        var page = Single(await DocxAsync(docx));

        var baselines = Lines(page).Select(l => l.Y).ToList();
        Assert.Equal(1.15f * LineHeight(RepositoryFonts.Latin, 10) + 10, baselines[1] - baselines[0], 0.5f);
    }

    // ───────────────────────── Word: pictures, page numbers, CJK (#280, #237, #66) ─────────────────────────

    [Fact]
    public async Task Issue280_a_cropped_picture_shows_only_what_the_crop_keeps()
    {
        // A picture whose left half Word crops away (a:srcRect l="50000"): only the right, blue half is drawn.
        var docx = Docx("");
        var id = docx.Image(HalfRedHalfBlue());
        var picture = InlinePicture(id, 2 * 914400, 914400).Replace("</pic:blipFill>", "<a:srcRect l=\"50000\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>", StringComparison.Ordinal);
        docx.Paragraph(P(picture));

        var page = Single(await DocxAsync(docx));

        using var pixels = page.Render(72 * Scale);
        int red = 0, blue = 0;
        // 144 × 72 pt from the top-left margin corner, less a 4 pt edge.
        for (var y = 76; y < 140; y += 2)
        {
            for (var x = 76; x < 212; x += 2)
            {
                var c = pixels.GetPixel(x * Scale, y * Scale);
                if (c.Red > 200 && c.Blue < 80)
                    red++;
                else if (c.Blue > 200 && c.Red < 80)
                    blue++;
            }
        }
        Assert.Equal(0, red);
        Assert.True(blue > 1500, $"{blue} blue samples");
    }

    [Fact]
    public async Task Issue280_a_section_restarts_its_page_numbers()
    {
        var docx = Docx("");
        var footer = docx.Footer(P(R("Page ") + Field("PAGE", "1")));
        var reference = $"<w:footerReference w:type=\"default\" r:id=\"{footer}\"/>";
        docx.Paragraph(P(R("front one")) + PageBreak);
        docx.Paragraph(P(R("front two"), $"<w:sectPr>{reference}{DefaultPage}</w:sectPr>"));
        docx.Paragraph(P(R("chapter")));
        docx.SectionProperties = $"{reference}{DefaultPage}<w:pgNumType w:start=\"1\"/>";

        var pages = await DocxAsync(docx);

        Assert.Equal(3, pages.Count);
        Assert.Equal(["Page1", "Page2", "Page1"], pages.Select(p => Lines(p).Last().Text));
    }

    [Fact]
    public async Task Issue280_degrees_celsius_before_a_comma_take_the_east_asian_font_without_overlapping()
    {
        // "25℃，" in a Chinese document: ℃ is an ambiguous-width sign, East Asian with the run's eastAsia hint, and rhwp
        // drew the comma over it.
        var docx = Docx(P(R("温度为25℃，湿度为60%。", "<w:rFonts w:hint=\"eastAsia\"/>")));

        var pages = await DocxAsync(docx);

        var celsius = Glyphs(pages[0]).Single(g => g.Char == '℃');
        Assert.Equal(RepositoryFonts.Korean, celsius.Run.Font.Typeface.FamilyName);
        var check = PdfChecks.OverlappingGlyphs(Pdf(pages));
        Assert.True(check.Ok, check.ToString());
    }

    [Fact]
    public async Task Issue280_the_rule_under_the_header_is_drawn()
    {
        var docx = Docx(P(R("body")));
        var header = docx.Header(P(R("Annual report"), "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"000000\"/></w:pBdr>"));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/>{DefaultPage}";

        var page = Single(await DocxAsync(docx));

        var head = Lines(page).Single(l => l.Text.Contains("Annual", StringComparison.Ordinal)).Y;
        using var pixels = page.Render(72 * Scale);
        Assert.Contains(DarkRows(pixels, LeftMargin + 300), y => y > head && y < 72);
    }

    [Fact]
    public async Task Issue237_cjk_quotes_and_character_indents_leave_no_overlaps()
    {
        // Chinese text with curly quotes in the East Asian font and a first-line indent of two characters.
        var docx = Docx(P(R("他说：“这是引号。”然后，他离开了。“文字”不应重叠。", "<w:rFonts w:hint=\"eastAsia\"/>"), "<w:ind w:firstLineChars=\"200\"/>"));

        var pages = await DocxAsync(docx);

        var first = Glyphs(pages[0]).MinBy(g => g.X);
        Assert.Equal(LeftMargin + 20, first.X, 1f);
        var check = PdfChecks.OverlappingGlyphs(Pdf(pages));
        Assert.True(check.Ok, check.ToString());
        var text = PdfChecks.TextRoundTrip(Pdf(pages), "他说：“这是引号。”然后，他离开了。“文字”不应重叠。");
        Assert.True(text.Ok, text.ToString());
    }

    [Fact]
    public async Task Issue66_closing_punctuation_never_starts_a_line_and_opening_punctuation_never_ends_one()
    {
        NeedFonts(RepositoryFonts.Korean);
        // How many full-width characters a line holds, from a paragraph of them.
        var probe = Single(await DocxAsync(Docx(P(R(new string('文', 100))))));
        var perLine = Lines(probe)[0].Text.Length;

        // A full stop right after a full line, and an opening bracket as the last character of one.
        var stop = new string('文', perLine) + "。" + new string('字', 10);
        var bracket = new string('文', perLine - 1) + "「字」" + new string('文', 10);
        var page = Single(await DocxAsync(Docx(P(R(stop)) + P(R(bracket)))));

        var lines = Lines(page).Select(l => l.Text).ToList();
        Assert.Equal(4, lines.Count);
        Assert.DoesNotContain(lines, l => "。，、）」』！？：；".Contains(l[0], StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => "（「『“".Contains(l[^1], StringComparison.Ordinal));
        // Nothing dropped on the way.
        Assert.Equal(stop + bracket, string.Concat(lines));
    }

    [Fact]
    public async Task Issue350_wingdings_check_boxes_come_out_as_ballot_boxes()
    {
        // ☑ and ☐ inserted as symbols (w:sym, Wingdings 2) and typed in Wingdings ("þ", "¨"): rhwp drew bullets.
        var docx = Docx(P(R("Agree ") + "<w:r><w:sym w:font=\"Wingdings 2\" w:char=\"F052\"/></w:r>" + R(" Disagree ") +
                          "<w:r><w:sym w:font=\"Wingdings 2\" w:char=\"F0A3\"/></w:r>") +
                        P(R("Yes ") + R("þ", "<w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\"/>") + R(" No ") +
                          R("¨", "<w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\"/>")));

        var page = Single(await DocxAsync(docx));

        Assert.Equal(["Agree☑Disagree☐", "Yes☑No☐"], Lines(page).Select(l => l.Text));
        Assert.DoesNotContain(Runs(page), r => r.Font.Typeface.FamilyName.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── Word: speed, narrow cells, fonts, fallback (#962, #556, #859, #413, #356) ─────────────────────────

    [Fact]
    public async Task Issue962_a_word_document_of_fifty_pages_lays_out_in_seconds()
    {
        // 12 s for 41 pages through rhwp, and rhwp's 120 s limit on long books (#127, #685).
        const string Sentence = "Filee lays out every paragraph of a long report with the fonts the document asks for, page after page. ";
        var docx = Docx(string.Concat(Enumerable.Range(0, 1200).Select(i => P(R($"{i}. {Sentence}")))));
        var path = docx.Save(Path.Combine(fx.NewFolder(), "long.docx"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pages = await ComposeAsync(path, "docx");
        var pdf = Pdf(pages);
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"{pages.Count} pages, {pdf.Length:N0} bytes in {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(pages.Count > 41, $"{pages.Count} pages");
        // About 3 s on a desktop PC, reading and writing included; CI's slowest runner takes several times that.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{pages.Count} pages took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Issue556_a_word_that_fits_its_narrow_cell_is_not_broken()
    {
        // "Documentació" in a 3 cm column came out as "Documentació/n" ... broken inside the word (#556, #859).
        var cells = string.Concat(new[] { "Documentació", "Sol·licitant" }.Select(t => $"<w:tc><w:tcPr><w:tcW w:w=\"1700\" w:type=\"dxa\"/></w:tcPr>{P(R(t))}</w:tc>"));
        var docx = Docx($"<w:tbl><w:tblPr><w:tblW w:w=\"3400\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1700\"/><w:gridCol w:w=\"1700\"/></w:tblGrid><w:tr>{cells}</w:tr></w:tbl>");

        var page = Single(await DocxAsync(docx));

        Assert.Single(Lines(page));
        Assert.Equal("Documentació", Words(page)[0]);
        Assert.Equal("Sol·licitant", Words(page)[1]);
    }

    [Fact]
    public void Issue413_fonts_the_computer_lacks_are_named_before_converting()
    {
        // The notice for a document in fonts that aren't installed (#413, #514, #1011, #1422); symbol fonts and fonts
        // with a metric-compatible substitute don't count.
        var docx = Docx(P(R("Aptos text", "<w:rFonts w:ascii=\"Aptos\" w:hAnsi=\"Aptos\"/>") + R("þ", "<w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\"/>") +
                          R("Calibri text", "<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/>")));
        var path = docx.Save(Path.Combine(fx.NewFolder(), "fonts.docx"));

        var missing = DocumentFonts.Missing(path, face => face is "Noto Sans" or "Noto Sans KR" or "Noto Sans Arabic" or "Carlito");

        Assert.Equal(["Aptos"], missing);
    }

    [Fact]
    public void Issue1011_a_font_the_font_table_gives_an_installed_other_name_for_is_not_missing()
    {
        // styles.xml names "Arial (CS Gövde)" and the font table says its other name is Arial: Word draws Arial, so
        // the notice named a font nothing was missing for. An other name that isn't installed either still counts.
        var docx = Docx(P(R("Body", "<w:rFonts w:ascii=\"Noto Sans\" w:hAnsi=\"Noto Sans\" w:cs=\"Arial (CS Gövde)\"/>") +
                          R("Odd", "<w:rFonts w:ascii=\"Odd Face\" w:hAnsi=\"Odd Face\"/>")));
        docx.FontTable = "<w:font w:name=\"Arial (CS Gövde)\"><w:altName w:val=\"Arial\"/><w:family w:val=\"roman\"/></w:font>" +
                         "<w:font w:name=\"Odd Face\"><w:altName w:val=\"Absent Face\"/></w:font>";
        var path = docx.Save(Path.Combine(fx.NewFolder(), "fonts.docx"));

        // Arial counts through its metric-compatible Liberation Sans.
        var missing = DocumentFonts.Missing(path, face => face is "Noto Sans" or "Noto Sans KR" or "Noto Sans Arabic" or "Liberation Sans");

        Assert.Equal(["Odd Face"], missing);
    }

    [Fact]
    public async Task Issue1011_text_in_a_font_that_is_not_installed_is_drawn_with_its_other_name()
    {
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Korean);
        var docx = Docx(P(R("Named", "<w:rFonts w:ascii=\"Absent Face (Body)\" w:hAnsi=\"Absent Face (Body)\"/>")) +
                        P(R("Plain", "<w:rFonts w:ascii=\"Other Absent Face\" w:hAnsi=\"Other Absent Face\"/>")));
        docx.FontTable = "<w:font w:name=\"Absent Face (Body)\"><w:altName w:val=\"Noto Sans KR\"/></w:font>";

        var runs = Runs(Single(await DocxAsync(docx))).ToList();

        Assert.Same(_fonts.Font(RepositoryFonts.Korean), runs.Single(r => r.Text == "Named").Font);
        // Without another name, the usual substitute.
        Assert.NotSame(_fonts.Font(RepositoryFonts.Korean), runs.Single(r => r.Text == "Plain").Font);
    }

    [Fact]
    public async Task Issue1011_a_number_followed_by_a_space_stays_on_its_line_in_a_narrow_cell()
    {
        // The answer key: "10." … "24." in a 2.2 cm cell, numbered with w:suff="space". A tab came after every number,
        // and a number wider than its hanging indent sent the answer to the next line; the table ran to two pages.
        var docx = Docx(Cell("<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr><w:ind w:left=\"433\" w:right=\"7\" w:hanging=\"264\"/><w:jc w:val=\"center\"/>"));
        docx.Numbering = Numbering(10, "<w:suff w:val=\"space\"/>");

        var page = Single(await DocxAsync(docx));

        Assert.Equal("10.D", Assert.Single(Lines(page)).Text);
    }

    [Fact]
    public async Task Issue1011_a_number_wider_than_its_hanging_indent_tabs_to_words_next_default_stop()
    {
        // With the tab Word puts after a number by default, a number past its hanging indent sends the text to the
        // next default tab stop: every half inch from the margin (w:defaultTabStop), not 한글's 40 pt from the indent.
        var docx = Docx(P(R("D"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr><w:ind w:left=\"1000\" w:hanging=\"200\"/>"));
        docx.Numbering = Numbering(10, "");

        var glyphs = Glyphs(Single(await DocxAsync(docx)));

        // The number from 40 pt is wider than the 10 pt to the hanging indent's stop at 50 pt.
        Assert.Equal(LeftMargin + 40, glyphs.Single(g => g.Char == '1').X, 0.1f);
        Assert.Equal(LeftMargin + 72, glyphs.Single(g => g.Char == 'D').X, 0.1f);
    }

    [Fact]
    public async Task Issue510_default_tab_stops_are_words_from_the_margin()
    {
        // A CV's "July 2026 – Present" after default tabs wrapped "Present" onto a line of its own: the tabs went to
        // 한글's stops, every 40 pt from the indent, instead of the document's w:defaultTabStop from the margin.
        var docx = Docx(P(R("a") + "<w:r><w:tab/></w:r>" + R("b"), "<w:ind w:left=\"300\"/>"));
        docx.Settings = "<w:defaultTabStop w:val=\"567\"/>";

        var glyphs = Glyphs(Single(await DocxAsync(docx)));

        // 1 cm stops: "a" from 15 pt, "b" at the first stop after it, 28.35 pt.
        Assert.Equal(LeftMargin + 15, glyphs.Single(g => g.Char == 'a').X, 0.1f);
        Assert.Equal(LeftMargin + 28.35f, glyphs.Single(g => g.Char == 'b').X, 0.1f);
    }

    /// <summary>A one-column table 1253 twips wide, its cell holding "D" in a paragraph with <paramref name="pPr"/>.</summary>
    private static string Cell(string pPr) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"1253\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1253\"/></w:tblGrid>" +
        $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"1253\" w:type=\"dxa\"/></w:tcPr>{P(R("D"), pPr)}</w:tc></w:tr></w:tbl>";

    /// <summary>A decimal list (numId 1) starting at <paramref name="start"/>, "%1.", with <paramref name="levelExtra"/>.</summary>
    private static string Numbering(int start, string levelExtra) =>
        $"<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"{start}\"/><w:numFmt w:val=\"decimal\"/>{levelExtra}" +
        "<w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num>";

    [Fact]
    public void Issue356_a_word_document_with_an_ole_object_keeps_its_fallback_route()
    {
        // A Visio drawing in a Word file: the renderer doesn't draw OLE objects yet, so the file goes to LibreOffice or rhwp.
        var ole = Docx(P(R("Diagram:") + "<w:r><w:object xmlns:o=\"urn:schemas-microsoft-com:office:office\"><o:OLEObject Type=\"Embed\" ProgID=\"Visio.Drawing.15\" ShapeID=\"_x0000_i1025\" DrawAspect=\"Content\" ObjectID=\"_1\" r:id=\"rId99\"/></w:object></w:r>"))
            .Save(Path.Combine(fx.NewFolder(), "visio.docx"));
        var plain = Docx(P(R("No objects"))).Save(Path.Combine(fx.NewFolder(), "plain.docx"));
        var renderer = new FileeRendererConverter();

        Assert.Equal("OLE objects and charts", renderer.FirstUnsupported(ole, "docx"));
        Assert.Null(renderer.FirstUnsupported(plain, "docx"));
    }

    [Fact]
    public void Issue1180_text_in_a_document_that_names_no_font_is_in_times_new_roman()
    {
        // A worksheet whose styles and defaults name no font at all: Word draws it in Times New Roman, the renderer
        // drew it in its own sans serif, wider, and the pages ran over. A theme or named font still wins.
        const string Bare = """
            <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault></w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="normal"/></w:style>
            """;
        var docx = new DocxBuilder { Styles = Bare }.Paragraph(P(R("Student Name")) + P(R("UID", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\"/>")));
        var themed = new DocxBuilder { Styles = Bare.Replace("<w:sz ", "<w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\"/><w:sz ", StringComparison.Ordinal) }.Paragraph(P(R("Body")));

        var plain = DocxReader.Read(docx.Save(Path.Combine(fx.NewFolder(), "bare.docx")), fx.NewFolder());
        var theme = DocxReader.Read(themed.Save(Path.Combine(fx.NewFolder(), "themed.docx")), fx.NewFolder());

        var texts = Assert.Single(plain.Sections).Blocks.OfType<HParagraph>().Select(p => p.Inlines.OfType<HText>().Single()).ToList();
        Assert.Equal("Times New Roman", texts[0].Format.Font);
        Assert.Equal("Times New Roman", texts[0].Format.Layout?.WordFonts?.Ascii);
        Assert.Equal("Times New Roman", texts[0].Format.Layout?.WordFonts?.ComplexScript);
        Assert.Equal("Arial", texts[1].Format.Font);
        var body = Assert.Single(theme.Sections).Blocks.OfType<HParagraph>().Single().Inlines.OfType<HText>().Single();
        Assert.NotEqual("Times New Roman", body.Format.Font);
    }

    // ───────────────────────── Word: fixes that came with the main merge (#1194, #355, #1237) ─────────────────────────

    [Fact]
    public async Task Issue1194_contextual_spacing_drops_the_space_between_list_items()
    {
        // Word's List Paragraph: 10 pt after, but none between paragraphs of the same style (22 pages, not 28).
        static string Styles(string contextual) => NotoStyles +
            $"<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"200\"/>{contextual}<w:ind w:left=\"720\"/></w:pPr></w:style>";
        var items = string.Concat(Enumerable.Range(1, 6).Select(i => P(R($"Item {i}"), "<w:pStyle w:val=\"ListParagraph\"/>")));

        var tight = Single(await DocxAsync(new DocxBuilder { Styles = Styles("<w:contextualSpacing/>") }.Paragraph(items)));
        var loose = Single(await DocxAsync(new DocxBuilder { Styles = Styles("") }.Paragraph(items)));

        var single = LineHeight(RepositoryFonts.Latin, 10);
        Assert.Equal(single, Pitch(tight), 0.1f);
        Assert.Equal(single + 10, Pitch(loose), 0.1f);
    }

    [Fact]
    public async Task Issue1194_a_page_break_at_the_end_of_a_paragraph_leaves_no_empty_line_on_the_next_page()
    {
        // Each chapter of the report starts after a paragraph holding only a page break. Word keeps that paragraph's
        // mark on the old page; the renderer put it on the new one, an empty line and 10 pt above every chapter.
        var styles = NotoStyles.Replace("<w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>",
            "<w:spacing w:after=\"200\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>", StringComparison.Ordinal);
        Assert.NotEqual(NotoStyles, styles);
        const string Break = "<w:r><w:br w:type=\"page\"/></w:r>";
        var own = new DocxBuilder { Styles = styles, SectionProperties = DefaultPage }.Paragraph(P(R("Chapter 1")) + P(Break) + P(R("Chapter 2")));
        var ending = new DocxBuilder { Styles = styles, SectionProperties = DefaultPage }.Paragraph(P(R("Chapter 1") + Break) + P(R("Chapter 2")));
        var property = new DocxBuilder { Styles = styles, SectionProperties = DefaultPage }.Paragraph(P(R("Chapter 1")) + P(R("Chapter 2"), "<w:pageBreakBefore/>"));
        var middle = new DocxBuilder { Styles = styles, SectionProperties = DefaultPage }.Paragraph(P(R("Chapter 1") + Break + R("Chapter 2")));

        var expected = Lines((await DocxAsync(property))[1]).Single().Y;
        foreach (var docx in new[] { own, ending, middle })
        {
            var pages = await DocxAsync(docx);
            Assert.Equal(2, pages.Count);
            var line = Assert.Single(Lines(pages[1]));
            Assert.Equal("Chapter2", line.Text);
            Assert.Equal(expected, line.Y, 0.1f);
        }
    }

    [Fact]
    public async Task Issue355_an_equation_number_after_a_hash_sits_at_the_right_margin()
    {
        // "a=b#(1)" in Word: the equation in the middle, "(1)" at the right margin; rhwp drew "#(1)" in the equation.
        const string Equation = "<m:oMathPara><m:oMath><m:eqArr><m:eqArrPr><m:maxDist m:val=\"1\"/></m:eqArrPr><m:e>" +
                                "<m:r><m:t>a=b</m:t></m:r><m:r><m:t>#</m:t></m:r><m:d><m:dPr/><m:e><m:r><m:t>1</m:t></m:r></m:e></m:d>" +
                                "</m:e></m:eqArr></m:oMath></m:oMathPara>";
        var pages = await DocxAsync(Docx(P(Equation, "<w:jc w:val=\"center\"/>")));

        var text = PdfLines.Text(Pdf(pages));
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        using var pdf = PigDocument.Open(Pdf(pages));
        var close = pdf.GetPage(1).Letters.Where(l => l.Value == ")").MaxBy(l => l.EndBaseLine.X)!;
        Assert.Equal(RightMargin, close.EndBaseLine.X, 1.5);
        var a = pdf.GetPage(1).Letters.First(l => l.Value == "a");
        Assert.InRange(a.StartBaseLine.X, 250, 300);
    }

    [Fact]
    public async Task Issue1237_css_in_a_style_element_colours_the_text()
    {
        var path = Path.Combine(fx.NewFolder(), "styled.html");
        await File.WriteAllTextAsync(path,
            "<html><head><style>p.warn { color: #ff0000; } .note { color: #0000ff }</style></head>" +
            "<body><p class=\"warn\">Warning text</p><p><span class=\"note\">Note text</span></p><p>Plain text</p></body></html>",
            new UTF8Encoding(false), Token);

        var page = Single(await ComposeAsync(path, "html"));

        Assert.Equal(new SKColor(0xFF, 0, 0), Runs(page).First(r => r.Text.Contains("Warning", StringComparison.Ordinal)).Color);
        Assert.Equal(new SKColor(0, 0, 0xFF), Runs(page).First(r => r.Text.Contains("Note", StringComparison.Ordinal)).Color);
        Assert.Equal(SKColors.Black, Runs(page).First(r => r.Text.Contains("Plain", StringComparison.Ordinal)).Color);
    }

    [Fact]
    public async Task Issue1313_a_frame_sits_where_its_page_position_puts_it()
    {
        // The form's verification code: a paragraph in a frame (w:framePr) 492 pt from the left and 720 pt from the top
        // of the page, 84 × 98 pt, centred. It came out under the signature in the text, in the middle of the page.
        var frame = "<w:framePr w:w=\"1680\" w:h=\"1961\" w:hRule=\"exact\" w:wrap=\"none\" w:vAnchor=\"page\" w:hAnchor=\"page\" w:x=\"9840\" w:y=\"14400\" w:anchorLock=\"1\"/><w:jc w:val=\"center\"/>";
        var docx = Docx(P(R("Signature")) + P(R("Code"), frame) + P(R("Check"), frame) + P(R("After")));

        var page = Single(await DocxAsync(docx));

        var lines = Lines(page);
        var signature = lines.Single(l => l.Text == "Signature");
        var after = lines.Single(l => l.Text == "After");
        Assert.Equal(LineHeight(RepositoryFonts.Latin, 10), after.Y - signature.Y, 0.5f);
        var codeLine = lines.Single(l => l.Text == "Code");
        var code = Glyphs(page).Where(g => g.Text.Length > 0 && MathF.Round(g.Y, 1) == codeLine.Y).ToList();
        Assert.Equal(4, code.Count);
        Assert.InRange(codeLine.Y, 720, 720 + 98);
        Assert.InRange(code.Min(g => g.X), 492, 492 + 84);
        Assert.InRange(code.Max(g => g.X), 492, 492 + 84);
        // Centred in the frame, the two paragraphs one under the other.
        Assert.InRange((code.Min(g => g.X) + code.Max(g => g.X)) / 2, 492 + 42 - 12, 492 + 42 + 12);
        var check = lines.Single(l => l.Text == "Check");
        Assert.True(check.Y > code[0].Y && check.Y < 720 + 98, $"second line at {check.Y}");
    }

    // ───────────────────────── Word: floating tables and paragraph spacing (#1194, #1152) ─────────────────────────

    /// <summary>A one-cell table (Table Grid) 2 inches wide holding <paramref name="text"/>, with <paramref name="position"/> in its properties.</summary>
    private static string OneCellTable(string text, string position = "") =>
        $"<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/>{position}<w:tblW w:w=\"2880\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2880\"/></w:tblGrid>" +
        $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"2880\" w:type=\"dxa\"/></w:tcPr>{P(R(text))}</w:tc></w:tr></w:tbl>";

    private static SKPoint Where(PageContent page, string text) => Runs(page).First(r => r.Text.Contains(text, StringComparison.Ordinal)).Positions[0];

    [Fact]
    public async Task Issue1194_text_after_a_floating_table_goes_around_it()
    {
        // The report's figure captions are floating tables (w:tblpPr), placed from the top of the paragraph after them;
        // the empty paragraphs after them sat beside the caption in Word and under it in Filee, a page longer in all.
        const string Right = "<w:tblpPr w:leftFromText=\"180\" w:rightFromText=\"180\" w:vertAnchor=\"text\" w:horzAnchor=\"margin\" w:tblpXSpec=\"right\" w:tblpY=\"200\"/>";
        var floating = Single(await DocxAsync(Docx(P(R("Before")) + OneCellTable("Cell", Right) + P(R("Beside")) + P(R("Next")))));
        var flowing = Single(await DocxAsync(Docx(P(R("Before")) + OneCellTable("Cell") + P(R("Beside")))));

        var (before, cell, beside, next) = (Where(floating, "Before"), Where(floating, "Cell"), Where(floating, "Beside"), Where(floating, "Next"));
        // The table at the right margin, 10 pt below the top of the paragraph after it.
        Assert.Equal(RightMargin - 144 + 5.4f, cell.X, 1f);
        var pitch = LineHeight(RepositoryFonts.Latin, 10);
        Assert.Equal(before.Y + pitch + 10, cell.Y, 1f);
        // The paragraphs after it on their own lines at the left margin, beside it.
        Assert.Equal(LeftMargin, beside.X, 0.5f);
        Assert.Equal(before.Y + pitch, beside.Y, 0.5f);
        Assert.Equal(beside.Y + pitch, next.Y, 0.5f);
        // In the flow the paragraph goes under the table.
        Assert.True(Where(flowing, "Beside").Y > Where(flowing, "Cell").Y + pitch - 0.5f);
    }

    [Fact]
    public async Task Issue1194_a_table_placed_on_the_page_leaves_the_text_where_it_is()
    {
        // A floating table 5 inches down the page (vertAnchor page) takes no room in the text above it.
        const string Page = "<w:tblpPr w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"1440\" w:tblpY=\"7200\"/>";
        var page = Single(await DocxAsync(Docx(P(R("Before")) + OneCellTable("Cell", Page) + P(R("After")))));

        var pitch = LineHeight(RepositoryFonts.Latin, 10);
        Assert.Equal(Where(page, "Before").Y + pitch, Where(page, "After").Y, 0.5f);
        Assert.InRange(Where(page, "Cell").Y, 360, 360 + pitch + 1);
    }

    // ───────────────────────── Word: right-to-left documents (#202, #216, #620) ─────────────────────────

    [Fact]
    public async Task Issue202_an_arabic_paragraph_starting_with_latin_text_reads_right_to_left()
    {
        NeedFonts(RepositoryFonts.Arabic);
        var page = Single(await DocxAsync(Docx(P(R("Filee 2026 ") + R("مرحبا بالعالم", "<w:rtl/>"), "<w:bidi/>"))));

        var glyphs = Glyphs(page);
        // Right-aligned (the start of a right-to-left paragraph), the Latin word first, so rightmost.
        var right = glyphs.Max(g => g.X + Width(g));
        Assert.Equal(RightMargin, right, 1.5f);
        Assert.True(glyphs.Single(g => g.Char == 'F').X > glyphs.Where(g => g.Char >= 0x0600).Max(g => g.X));
    }

    [Fact]
    public async Task Issue216_a_right_to_left_table_puts_its_first_column_on_the_right()
    {
        var cells = string.Concat(new[] { "first", "second" }.Select(t => $"<w:tc><w:tcPr><w:tcW w:w=\"3000\" w:type=\"dxa\"/></w:tcPr>{P(R(t))}</w:tc>"));
        var page = Single(await DocxAsync(Docx($"<w:tbl><w:tblPr><w:bidiVisual/><w:tblW w:w=\"6000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr>{cells}</w:tr></w:tbl>")));

        var glyphs = Glyphs(page);
        Assert.True(Span(glyphs, "first").Left > Span(glyphs, "second").Right);
    }

    [Fact]
    public async Task Issue620_a_footnote_number_stays_between_its_brackets_in_right_to_left_notes()
    {
        NeedFonts(RepositoryFonts.Arabic);
        var docx = Docx(P(R("نص المتن", "<w:rtl/>") + "<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r>", "<w:bidi/>"));
        docx.Footnotes("<w:footnote w:id=\"1\">" +
                       P(R("(", "<w:rtl/>") + "<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteRef/></w:r>" + R(") نص الحاشية", "<w:rtl/>"), "<w:bidi/>") +
                       "</w:footnote>");

        var page = Single(await DocxAsync(docx));

        // The note's line: the last on the page. Its "1" between "(" and ")", wherever bidi puts the pair.
        var note = Glyphs(page).Where(g => g.Y > Lines(page)[0].Y + 20).ToList();
        var one = Assert.Single(note, g => g.Char == '1');
        var brackets = note.Where(g => g.Char is '(' or ')').Select(g => g.X).Order().ToList();
        Assert.Equal(2, brackets.Count);
        Assert.InRange(one.X, brackets[0], brackets[1]);
    }

    // ───────────────────────── HWP / HWPX (#49, #65, main #1140, #1416) ─────────────────────────

    [Fact]
    public void Issue49_a_picture_behind_text_leaves_the_lines_where_they_are()
    {
        NeedFonts(RepositoryFonts.Latin);
        // A form with a picture "behind text" anchored in its second paragraph: rhwp moved the next line down.
        HDocument Form(bool picture)
        {
            var section = new HSection { Page = PageComposer.DefaultPage };
            for (var i = 0; i < 6; i++)
            {
                var paragraph = Paragraph($"Form line {i}");
                if (picture && i == 1)
                    paragraph.Inlines.Insert(0, Box(20000, 10000, new HAnchor("PARA", "LEFT", 2000, "PARA", "TOP", 0, "BEHIND_TEXT", true)));
                section.Blocks.Add(paragraph);
            }
            var document = new HDocument();
            document.Sections.Add(section);
            return document;
        }

        var without = Single(Compose(Form(picture: false)));
        var with = Single(Compose(Form(picture: true)));

        Assert.Equal(Lines(without).Select(l => l.Y), Lines(with).Select(l => l.Y));
        // The picture is drawn, under the text.
        Assert.Contains(with.Items, i => i is DrawingItem);
    }

    [Fact]
    public void Issue49_a_table_set_as_a_character_is_centred_with_its_outer_margins()
    {
        NeedFonts(RepositoryFonts.Latin);
        // A title box set as a character in a centred paragraph: 한글 centres it with its outer margins, as a character
        // that wide. The reader gives it the paragraph's alignment (DocumentModelTests); here, where it is drawn.
        float TextLeft(HAlign align)
        {
            var cell = new HCell();
            cell.Blocks.Add(Paragraph("Title"));
            var row = new HRow();
            row.Cells.Add(cell);
            var table = new HTable { ColumnCount = 1, ColumnWidths = [6000], AsCharacter = true, Align = align, OuterMargin = new HInsets(2000, 0, 0, 0) };
            table.Rows.Add(row);
            var section = new HSection { Page = PageComposer.DefaultPage };
            section.Blocks.Add(table);
            var document = new HDocument();
            document.Sections.Add(section);
            return Glyphs(Single(Compose(document))).Min(g => g.X);
        }

        // The page's text is 425.2 pt wide; the table 60 pt, with 20 pt of margin on its left.
        Assert.Equal((425.2f - 60 - 20) / 2, TextLeft(HAlign.Center) - TextLeft(HAlign.Left), 0.5f);
        Assert.Equal(425.2f - 60 - 20, TextLeft(HAlign.Right) - TextLeft(HAlign.Left), 0.5f);
    }

    [Fact]
    public void Issue49_a_picture_set_as_a_character_follows_its_paragraphs_alignment()
    {
        NeedFonts(RepositoryFonts.Latin);
        // Pictures and shapes set as characters sit on their line like letters, so a centred or right-aligned
        // paragraph moves them as it does text.
        (float Left, float Right) Drawn(HAlign align)
        {
            var paragraph = Paragraph("");
            paragraph.Format = paragraph.Format with { Align = align };
            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new HShape(HShapeKind.Rectangle) { Width = 6000, Height = 2000, Fill = "#000000", Line = HBorder.None });
            var section = new HSection { Page = PageComposer.DefaultPage };
            section.Blocks.Add(paragraph);
            var document = new HDocument();
            document.Sections.Add(section);
            using var pixels = Single(Compose(document)).Render(72 * Scale);
            // The shape is on the page's first line, in its top third.
            var columns = Enumerable.Range(0, pixels.Width).Where(x => Enumerable.Range(0, pixels.Height / 3).Any(y => pixels.GetPixel(x, y).Red < 128)).ToList();
            return (columns[0] / (float)Scale, (columns[^1] + 1) / (float)Scale);
        }

        // 85.04 pt of left margin and 425.2 pt of text: the 60 pt shape from 267.64 pt centred, to 510.24 pt at the right.
        Assert.Equal(85.04f + (425.2f - 60) / 2, Drawn(HAlign.Center).Left, 1f);
        Assert.Equal(85.04f + 425.2f, Drawn(HAlign.Right).Right, 1f);
        Assert.Equal(85.04f, Drawn(HAlign.Left).Left, 1f);
    }

    [Fact]
    public void Issue65_a_hangul_paragraph_box_is_drawn_around_its_paragraphs()
    {
        NeedFonts(RepositoryFonts.Korean);
        var line = new HBorder(HBorderStyle.Solid, 0.4, "#000000");
        var box = new HParaBorder(new HBorders(line, line, line, line), null, new HInsets(500, 500, 300, 300), Connect: true);
        var section = new HSection { Page = PageComposer.DefaultPage };
        section.Blocks.Add(Paragraph("문제 1. 다음을 읽고 물음에 답하시오."));
        foreach (var text in new[] { "<보기>", "ㄱ. 첫째 보기", "ㄴ. 둘째 보기" })
        {
            var paragraph = Paragraph(text);
            paragraph.Format = paragraph.Format with { Layout = new HParaLayout(Border: box) };
            section.Blocks.Add(paragraph);
        }
        var document = new HDocument();
        document.Sections.Add(section);

        var page = Single(Compose(document));

        var lines = Lines(page);
        using var pixels = page.Render(72 * Scale);
        // One box: a line above "<보기>" and one below "ㄴ. 둘째 보기", none between them.
        var rows = DarkRows(pixels, 400).Where(y => y > lines[0].Y).ToList();
        Assert.Contains(rows, y => y < lines[1].Y - 8);
        Assert.Contains(rows, y => y > lines[3].Y);
        Assert.DoesNotContain(rows, y => y > lines[1].Y + 4 && y < lines[3].Y - 12);
    }

    [Fact]
    public void Issue1140_a_hangul_section_restarts_its_page_numbers()
    {
        NeedFonts(RepositoryFonts.Latin);
        var document = new HDocument();
        foreach (var (texts, restart) in new[] { (new[] { "front one", "front two" }, (int?)null), (["chapter"], 1) })
        {
            var section = new HSection { Page = PageComposer.DefaultPage, StartPageNumber = restart };
            for (var i = 0; i < texts.Length; i++)
            {
                var paragraph = Paragraph(texts[i]);
                paragraph.PageBreakBefore = i > 0;
                section.Blocks.Add(paragraph);
            }
            var footer = new HHeaderFooter(HPageType.Both);
            var number = Paragraph("Page ");
            number.Inlines.Add(new HField(HFieldKind.PageNumber, Format()));
            footer.Blocks.Add(number);
            section.Footers.Add(footer);
            document.Sections.Add(section);
        }
        var path = Path.Combine(fx.NewFolder(), "sections.hwpx");
        HwpxWriter.Write(document, path);

        var pages = Compose(HwpxReader.ReadForLayout(path, Path.Combine(fx.NewFolder(), "media")));

        Assert.Equal(["Page1", "Page2", "Page1"], pages.Select(p => Lines(p).Last().Text));
    }

    [Fact]
    public async Task Issue1416_line_breaks_inside_an_hwpx_paragraph_survive_txt_and_markdown()
    {
        // 한글's Shift+Enter (hp:lineBreak inside hp:t): Unhwp joined the lines.
        var paragraph = Paragraph("주소: 서울시");
        paragraph.Inlines.Add(new HLineBreak(Format()));
        paragraph.Inlines.Add(new HText("전화: 02-123-4567", Format()));
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { paragraph, Paragraph("다음 문단") } });
        var hwpx = Path.Combine(fx.NewFolder(), "lines.hwpx");
        HwpxWriter.Write(document, hwpx);

        var txt = (await File.ReadAllTextAsync(await ConvertAsync(hwpx, "txt"), Token)).ReplaceLineEndings("\n");
        var md = (await File.ReadAllTextAsync(await ConvertAsync(hwpx, "md"), Token)).ReplaceLineEndings("\n");

        Assert.Contains("주소: 서울시\n전화: 02-123-4567\n\n다음 문단", txt);
        Assert.Contains("주소: 서울시\\\n전화: 02-123-4567\n\n다음 문단", md);
    }

    // ───────────────────────── Slides (#1352, #979) ─────────────────────────

    [Fact]
    public void Issue1352_a_bottom_anchored_title_ends_in_its_box_above_the_subtitle()
    {
        NeedFonts(RepositoryFonts.Latin);
        // The Title Slide layout's title is anchored at the bottom of a 188 pt box (88.4–276.4 pt) with 3.6 pt insets,
        // the subtitle's box starts at 283.6 pt. rhwp drew the title a line low, over the subtitle.
        var deck = new SlideDeckBuilder();
        foreach (var title in new[] { "Annual report", "Annual report of the year" })
            deck.Slide(deck.Placeholder("ctrTitle", SlideDeckBuilder.Paragraph(title)) + deck.Placeholder("subTitle", SlideDeckBuilder.Paragraph("Filee team"), 1), SlideDeckBuilder.Layout.Title);
        var read = PptxSlideReader.Read(deck.Save(Path.Combine(fx.NewFolder(), "titles.pptx")), cancellationToken: Token);

        using var renderer = new SlideRenderer(new FontResolver(_fonts), _shaper, Token);
        var descent = Descent(RepositoryFonts.Latin, 60);
        foreach (var slide in read.Slides)
        {
            var page = renderer.Render(read, slide);
            var glyphs = Glyphs(page);
            var title = glyphs.Where(g => g.Run.Size > 50).Select(g => g.Y).Distinct().Order().ToList();
            var subtitle = glyphs.Where(g => g.Run.Size < 30).Min(g => g.Y);

            // The last line's descent ends at the box's bottom inset, not below it.
            Assert.InRange(title[^1] + descent, 276.4f - 3.6f - 8, 276.4f + 0.5f);
            Assert.True(title[0] > 88.4f + 40, $"first title baseline {title[0]}");
            // The subtitle's first line is below the title's descent, clear of it.
            Assert.True(subtitle - 24 * 0.75f > title[^1] + descent, $"title {title[^1]}, subtitle {subtitle}");
        }
    }

    [Fact]
    public void Issue979_arabic_text_on_a_slide_and_on_its_master_reads_right_to_left()
    {
        NeedFonts(RepositoryFonts.Arabic);
        const string Arabic = "<a:p><a:pPr algn=\"r\" rtl=\"1\"/><a:r><a:rPr lang=\"ar-SA\"/><a:t>مرحبا بالعالم</a:t></a:r></a:p>";
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.TextBox(100, 200, 600, 80, Arabic));
        var path = deck.Save(Path.Combine(fx.NewFolder(), "arabic.pptx"));
        // Text on the slide master too: the deck of #979 had its footer there.
        AddToMaster(path, $"<p:sp><p:nvSpPr><p:cNvPr id=\"90\" name=\"MasterText\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr>{SlideDeckBuilder.Xfrm(100, 400, 600, 80)}" +
                             $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr wrap=\"square\"/><a:lstStyle/>{Arabic.Replace("مرحبا بالعالم", "شركة فايلي للتحويل", StringComparison.Ordinal)}</p:txBody></p:sp>");
        var read = PptxSlideReader.Read(path, cancellationToken: Token);

        using var renderer = new SlideRenderer(new FontResolver(_fonts), _shaper, Token);
        var glyphs = Glyphs(renderer.Render(read, Assert.Single(read.Slides)));

        // The first word of each text to the right of the second, and the slide's text at the box's right side.
        Assert.True(Span(glyphs, "مرحبا").Left > Span(glyphs, "بالعالم").Right);
        Assert.True(Span(glyphs, "شركة").Left > Span(glyphs, "فايلي").Right);
        Assert.True(Span(glyphs, "فايلي").Left > Span(glyphs, "للتحويل").Right);
        var slideText = glyphs.Where(g => g.Y < 300).ToList();
        Assert.Equal(700 - 7.2f, slideText.Max(g => g.X + Width(g)), 2f);
    }

    // ───────────────────────── Sheets (#993, #67, #407, #1249, #778) ─────────────────────────

    [Fact]
    public void Issue993_a_merged_cell_split_by_a_manual_column_break_prints_both_pages()
    {
        // "Index was outside the bounds of the array": merged A1:D1 with a page break after column B.
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "Merged title"), book.Text("F1", "right")),
            after: "<mergeCells count=\"1\"><mergeCell ref=\"A1:D1\"/></mergeCells><pageSetup paperSize=\"9\"/>" +
                   "<colBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"2\" max=\"1048575\" man=\"1\"/></colBreaks>");

        var pages = PrintSheet(book);

        Assert.Equal(2, pages.Count);
        Assert.Contains("Merged", pages[0], StringComparison.Ordinal);
        Assert.Contains("right", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Issue407_fit_to_width_and_print_titles_keep_every_page_readable()
    {
        // A wide price list (#67, #407, #1249): fit to one page wide, the header row repeated on every page.
        var book = new XlsxBuilder();
        var rows = new StringBuilder(XlsxBuilder.Row(1, [.. Enumerable.Range(0, 20).Select(c => book.Text($"{SheetPageRenderer.ColumnName(c)}1", $"H{c + 1}"))]));
        for (var r = 2; r <= 300; r++)
            rows.Append(XlsxBuilder.Row(r, [.. Enumerable.Range(0, 20).Select(c => XlsxBuilder.Number($"{SheetPageRenderer.ColumnName(c)}{r}", r * 100 + c))]));
        book.Sheet("Prices", rows.ToString(), before: "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>",
            after: "<pageSetup paperSize=\"9\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        book.DefinedNames = "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Prices!$1:$1</definedName></definedNames>";

        var pages = PrintSheet(book);

        Assert.True(pages.Count > 1);
        Assert.All(pages, page =>
        {
            Assert.StartsWith("H1 H2", page, StringComparison.Ordinal);
            Assert.Contains("H20", page, StringComparison.Ordinal);
        });
        Assert.Contains("30019", pages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Issue778_chinese_text_in_a_sheet_is_drawn_with_a_font_that_has_it()
    {
        Assert.SkipWhen(SKFontManager.Default.MatchCharacter('中') is null, "No font on this computer has Chinese characters.");
        // Cells in a Latin font with Chinese text (#993, #778): rhwp left the characters out.
        var book = new XlsxBuilder();
        book.Sheet("数据", XlsxBuilder.Row(1, book.Text("A1", "产品名称"), book.Text("B1", "中文字符")), after: "<pageSetup paperSize=\"9\"/>");

        using var printed = SheetPrinter.Print(XlsxReader.Read(Save(book)), new SheetPrintSource("book.xlsx", ""), DocumentLimits.Default, DateTime.Now, null, Token);

        var glyphs = printed.Pages.SelectMany(Runs).Where(r => r.Text.Any(c => c >= 0x4E00)).ToList();
        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, r => Assert.DoesNotContain((ushort)0, r.Glyphs));
    }

    [Fact]
    public void Issue1249_ipa_symbols_in_a_sheet_are_measured_and_dont_overlap()
    {
        // A vocabulary list with IPA (rhwp couldn't measure these characters and drew them over each other).
        var book = new XlsxBuilder();
        var rows = string.Concat(new[] { ("phonetic", "fəˈnɛtɪk"), ("thought", "θɔːt"), ("measure", "ˈmɛʒə"), ("sing", "sɪŋ") }
            .Select((w, i) => XlsxBuilder.Row(i + 1, book.Text($"A{i + 1}", w.Item1), book.Text($"B{i + 1}", w.Item2))));
        book.Sheet("Words", rows, before: "<cols><col min=\"1\" max=\"2\" width=\"20\" customWidth=\"1\"/></cols>", after: "<pageSetup paperSize=\"9\"/>");

        var pdf = SheetPrinter.ToPdf(XlsxReader.Read(Save(book)), new SheetPrintSource("words.xlsx", ""), Token);

        var text = PdfChecks.TextRoundTrip(pdf, "phonetic fəˈnɛtɪk thought θɔːt measure ˈmɛʒə sing sɪŋ");
        Assert.True(text.Ok, text.ToString());
        var overlaps = PdfChecks.OverlappingGlyphs(pdf);
        Assert.True(overlaps.Ok, overlaps.ToString());
    }

    // ───────────────────────── Markdown and text (#406, #1098, #1207, #1262) ─────────────────────────

    [Fact]
    public async Task Issue406_a_code_block_is_one_shaded_box_without_stripes()
    {
        NeedFonts(RepositoryFonts.Latin);
        var page = Single(await MarkdownAsync("Code:\n\n```\nfirst line\n\nthird line\n```\n\nAfter.\n"));

        var lines = Lines(page);
        var first = lines.Single(l => l.Text == "firstline").Y;
        var third = lines.Single(l => l.Text == "thirdline").Y;
        using var pixels = page.Render(72 * Scale);
        // Down the box's left part, from above the first line to below the last: shaded all the way, the empty line too.
        var x = (int)((Glyphs(page).Where(g => MathF.Round(g.Y, 1) == first).Min(g => g.X) - 2) * Scale);
        for (var y = (int)((first - 8) * Scale); y < (int)((third + 2) * Scale); y++)
        {
            var c = pixels.GetPixel(x, y);
            Assert.True(c.Red < 250 || c.Blue < 250, $"white at {y / (float)Scale} pt");
        }
    }

    [Fact]
    public async Task Issue1098_a_block_quote_has_its_bar_and_background()
    {
        NeedFonts(RepositoryFonts.Latin);
        var page = Single(await MarkdownAsync("Before.\n\n> A quoted sentence.\n\nAfter.\n"));

        var quote = Lines(page).Single(l => l.Text.StartsWith("Aquoted", StringComparison.Ordinal)).Y;
        var start = Glyphs(page).Where(g => MathF.Round(g.Y, 1) == quote).Min(g => g.X);
        using var pixels = page.Render(72 * Scale);
        // A grey bar left of the text, and the light background behind it.
        var bar = Enumerable.Range((int)(LeftMargin * Scale), (int)((start - LeftMargin) * Scale)).Select(x => pixels.GetPixel(x, (int)((quote - 3) * Scale)));
        Assert.Contains(bar, c => c.Red < 200);
        var fill = pixels.GetPixel((int)((start + 200) * Scale), (int)((quote - 3) * Scale));
        Assert.NotEqual(SKColors.White, fill);
    }

    [Fact]
    public async Task Issue1207_english_in_chinese_markdown_keeps_its_own_letter_widths()
    {
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Korean);
        // rhwp measured English in a CJK font as 0.5 em a letter: evenly spaced, stretched words.
        var page = Single(await MarkdownAsync("# 标题\n\n这是一个测试 minimum illicit 的段落，包含 `inline code` 和英文。\n"));

        var glyphs = Glyphs(page);
        var word = glyphs.Where(g => g.Run.Text.Contains("minimum", StringComparison.Ordinal)).ToList();
        var m = word.First(g => g.Char == 'm');
        var i = word.First(g => g.Char == 'i');
        Assert.True(Width(m) > 2 * Width(i), $"m {Width(m)}, i {Width(i)}");
        var check = PdfChecks.OverlappingGlyphs(Pdf([page]));
        Assert.True(check.Ok, check.ToString());
    }

    [Fact]
    public async Task Issue1262_cyrillic_text_is_spaced_without_glyphs_running_together()
    {
        NeedFonts(RepositoryFonts.Latin);
        const string Sentence = "Съешь же ещё этих мягких французских булок, да выпей чаю. ";
        var path = Path.Combine(fx.NewFolder(), "example.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat(Sentence, 12)) + "\n", new UTF8Encoding(false), Token);

        var pages = await ComposeAsync(path, "txt");

        var pdf = Pdf(pages);
        var overlaps = PdfChecks.OverlappingGlyphs(pdf);
        Assert.True(overlaps.Ok, overlaps.ToString());
        var text = PdfChecks.TextRoundTrip(pdf, string.Concat(Enumerable.Repeat(Sentence, 12)));
        Assert.True(text.Ok, text.ToString());
        // Words keep a space between them: "Съешь же" is two words on the page.
        Assert.Contains("Съешь", Words(pages[0]));
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Pixels per point pages are drawn at for the pixel checks.</summary>
    private const int Scale = 4;

    private const string PageBreak = "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";

    /// <summary>A drawn glyph: the text of its cluster (empty for the cluster's further glyphs) and its first character.</summary>
    private readonly record struct Glyph(string Text, int Index, float X, float Y, GlyphRun Run, int GlyphIndex)
    {
        public char Char => Text.Length > 0 ? Text[0] : '\0';
    }

    private void NeedFonts(params string[] families) =>
        Assert.SkipUnless(families.All(_fonts.Has), $"{string.Join(", ", families)} not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private DocxBuilder Docx(string body, string page = DefaultPage) =>
        new DocxBuilder { Styles = NotoStyles, SectionProperties = page }.Paragraph(body);

    private Task<List<PageContent>> DocxAsync(DocxBuilder docx) => ComposeAsync(docx.Save(Path.Combine(fx.NewFolder(), "case.docx")), "docx");

    private async Task<List<PageContent>> MarkdownAsync(string markdown)
    {
        var path = Path.Combine(fx.NewFolder(), "case.md");
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false), Token);
        return await ComposeAsync(path, "md");
    }

    /// <summary>A document of another format read as the renderer reads it and laid out against the template's styles.</summary>
    private async Task<List<PageContent>> ComposeAsync(string path, string format)
    {
        NeedFonts(RepositoryFonts.Latin);
        var document = await DocumentReaders.ReadAsync(path, format, fx.NewFolder(), Token);
        return Compose(document, StyleResolver.FromTemplate());
    }

    private List<PageContent> Compose(HDocument document, StyleResolver? styles = null) =>
        new PageComposer(new FontResolver(_fonts), _shaper, document.Kinsoku, styles).Compose(document, Token).Pages;

    private static byte[] Pdf(List<PageContent> pages)
    {
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, pages, cancellationToken: Token);
        return output.ToArray();
    }

    private async Task<string> ConvertAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private string Save(XlsxBuilder book) => book.Save(Path.Combine(fx.NewFolder(), "book.xlsx"));

    /// <summary>
    /// The printed pages' words, one string per page. The sheet's 맑은 고딕 is drawn with Noto Sans KR, whose locl
    /// swaps a digit after a Latin letter for a glyph the cmap doesn't reach (as Noto Sans CJK does on Linux).
    /// </summary>
    private List<string> PrintSheet(XlsxBuilder book)
    {
        NeedFonts(RepositoryFonts.Latin, RepositoryFonts.Korean);
        var pdf = SheetPrinter.ToPdf(XlsxReader.Read(Save(book)), new SheetPrintSource("book.xlsx", ""), Token, fonts: new FontResolver(_fonts));
        using var document = PigDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    private static PageContent Single(List<PageContent> pages) => Assert.Single(pages);

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    /// <summary>Every glyph of a page with the character its cluster starts at, where it is drawn on the page.</summary>
    private static List<Glyph> Glyphs(PageContent page)
    {
        var glyphs = new List<Glyph>();
        foreach (var run in Runs(page))
        {
            var starts = run.Clusters.Append(run.Text.Length).Distinct().Order().ToList();
            var seen = new HashSet<int>();
            for (var i = 0; i < run.Glyphs.Length; i++)
            {
                var cluster = run.Clusters[i];
                var at = run.Transform is { } transform ? transform.MapPoint(run.Positions[i]) : run.Positions[i];
                if (cluster >= run.Text.Length)
                    continue;
                var end = starts.First(s => s > cluster);
                glyphs.Add(new Glyph(seen.Add(cluster) ? run.Text[cluster..end] : "", cluster, at.X, at.Y, run, i));
            }
        }
        return glyphs;
    }

    /// <summary>The lines of a page top to bottom: baseline and characters left to right, without spaces.</summary>
    private static List<(float Y, string Text)> Lines(PageContent page) =>
    [
        .. Glyphs(page).Where(g => g.Text.Length > 0 && !char.IsWhiteSpace(g.Char)).GroupBy(g => MathF.Round(g.Y, 1)).OrderBy(g => g.Key)
            .Select(g => (g.Key, string.Concat(g.OrderBy(x => x.X).Select(x => x.Text)))),
    ];

    /// <summary>The page's words: glyphs split where a gap of a quarter em or more opens on a line.</summary>
    private static List<string> Words(PageContent page)
    {
        var words = new List<string>();
        foreach (var line in Glyphs(page).Where(g => g.Text.Length > 0 && !char.IsWhiteSpace(g.Char)).GroupBy(g => MathF.Round(g.Y, 1)).OrderBy(g => g.Key))
        {
            var word = new StringBuilder();
            Glyph? previous = null;
            foreach (var glyph in line.OrderBy(g => g.X))
            {
                if (previous is { } p && glyph.X - (p.X + Width(p)) > glyph.Run.Size / 5 && word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
                word.Append(glyph.Text);
                previous = glyph;
            }
            words.Add(word.ToString());
        }
        return words;
    }

    private static string Text(PageContent page) => string.Concat(Lines(page).Select(l => l.Text));

    /// <summary>The distance between the first two baselines of a page.</summary>
    private static float Pitch(PageContent page)
    {
        var lines = Lines(page);
        return lines[1].Y - lines[0].Y;
    }

    /// <summary>A glyph's advance in its run's font and size (unhinted: FreeType rounds hinted advances to whole pixels).</summary>
    private static float Width(Glyph glyph)
    {
        using var font = new SKFont(glyph.Run.Font.Typeface, glyph.Run.Size) { ScaleX = glyph.Run.ScaleX, Hinting = SKFontHinting.None, LinearMetrics = true };
        return font.GetGlyphWidths([glyph.Run.Glyphs[glyph.GlyphIndex]])[0];
    }

    /// <summary>From the left of the leftmost glyph to the right of the rightmost of the run that draws <paramref name="word"/>.</summary>
    private static (float Left, float Right) Span(List<Glyph> glyphs, string word)
    {
        var drawn = glyphs.Where(g => g.Run.Text == word).ToList();
        Assert.NotEmpty(drawn);
        return (drawn.Min(g => g.X), drawn.Max(g => g.X + Width(g)));
    }

    /// <summary>A character's advance in a font, unhinted (FreeType on Linux rounds hinted advances to whole pixels).</summary>
    private float Advance(string family, char c, float size)
    {
        using var font = new SKFont(_fonts.Font(family).Typeface, size) { Hinting = SKFontHinting.None, LinearMetrics = true };
        return font.MeasureText(c.ToString());
    }

    /// <summary>Word's single line height: the font's win ascent and descent.</summary>
    private float LineHeight(string family, float size)
    {
        var metrics = _shaper.Metrics(_fonts.Font(family));
        return (metrics.WinAscent + metrics.WinDescent) * size / metrics.UnitsPerEm;
    }

    private float Descent(string family, float size)
    {
        var metrics = _shaper.Metrics(_fonts.Font(family));
        return metrics.WinDescent * size / metrics.UnitsPerEm;
    }

    /// <summary>The page rows (in points) where the pixel column at <paramref name="x"/> points turns dark.</summary>
    private static List<float> DarkRows(SKBitmap pixels, float x)
    {
        var rows = new List<float>();
        var column = (int)(x * Scale);
        var dark = false;
        for (var y = 0; y < pixels.Height; y++)
        {
            var now = pixels.GetPixel(column, y).Red < 128;
            if (now && !dark)
                rows.Add(y / (float)Scale);
            dark = now;
        }
        return rows;
    }

    /// <summary>A 200 × 100 PNG, red on the left half and blue on the right.</summary>
    private static byte[] HalfRedHalfBlue()
    {
        using var bitmap = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, 100, 100, red);
            canvas.DrawRect(100, 0, 100, 100, blue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Adds <paramref name="shape"/> to the slide master's shape tree of a saved deck.</summary>
    private static void AddToMaster(string path, string shape)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("ppt/slideMasters/slideMaster1.xml")!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        entry.Delete();
        var at = xml.IndexOf("</p:spTree>", StringComparison.Ordinal);
        using var writer = new StreamWriter(zip.CreateEntry("ppt/slideMasters/slideMaster1.xml").Open(), new UTF8Encoding(false));
        writer.Write(xml.Insert(at, shape));
    }

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    /// <summary>A paragraph of the model with every format member set (no style to resolve), as 한글's are.</summary>
    private static HParagraph Paragraph(string text)
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        paragraph.Inlines.Add(new HText(text, Format()));
        return paragraph;
    }

    /// <summary>A grey rectangle <paramref name="width"/> × <paramref name="height"/> (HWPUNIT), floating.</summary>
    private static HShape Box(int width, int height, HAnchor anchor) =>
        new(HShapeKind.Rectangle) { Width = width, Height = height, Fill = "#C0C0C0", Line = HBorder.None, Anchor = anchor };
}
