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

    [Fact(Skip = "#510: found by this test: a Word document resolved against the built-in template takes its charPr useFontSpace=0, so a space is half the font size (5 pt at 10 pt; the font's own is 2.6 pt); fixed on branch p4/q2-cjk (75ef6d0a, StyleResolver)")]
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

    [Fact(Skip = "#510, #280: paragraph borders are not drawn yet (backlog P0-2, branch p4/q1-borders); a heading's bottom rule is lost")]
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

    [Fact(Skip = "#1152, #280: a first-page header with content is replaced by the default header (backlog P0-3, main #1159, branch p4/q2-headers)")]
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

    [Fact(Skip = "#1152, #416: a header taller than the top margin is drawn over the body instead of moving it down (backlog P0-3, main #1156, branch p4/q2-headers)")]
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

    [Fact(Skip = "#1152: a continuous section's header shows from the section's first page, before the break (backlog P0-3, main #1158, branch p4/q2-headers)")]
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
        // Word's Normal (10 pt after, 1.15 lines) under a table style that sets single spacing and no space after:
        // the rows are one line high (main #1394).
        const string Spaced = """
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="200" w:line="276" w:lineRule="auto"/></w:pPr></w:style>
            <w:style w:type="table" w:styleId="Compact"><w:name w:val="Compact"/><w:basedOn w:val="TableNormal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:style>
            """;
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i => $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4000\" w:type=\"dxa\"/></w:tcPr>{P(R($"Row {i}"))}</w:tc></w:tr>"));
        var docx = Docx($"<w:tbl><w:tblPr><w:tblStyle w:val=\"Compact\"/><w:tblW w:w=\"4000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid>{rows}</w:tbl>" + P(R("after")));
        docx.Styles = NotoStyles.Replace("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>", Spaced, StringComparison.Ordinal);

        var page = Single(await DocxAsync(docx));

        var baselines = Lines(page).Where(l => l.Text.StartsWith("Row", StringComparison.Ordinal)).Select(l => l.Y).ToList();
        Assert.Equal(5, baselines.Count);
        var single = LineHeight(RepositoryFonts.Latin, 10);
        Assert.All(baselines.Zip(baselines.Skip(1), (a, b) => b - a), pitch => Assert.InRange(pitch, single - 0.5f, single + 2));
    }

    [Fact(Skip = "#1152: found by this test: a table cell is as high as its last line's ink (TableLayout.InkHeight), so the space after a cell's last paragraph (Word's Normal: 10 pt) is left out of the row, which Word counts")]
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

    [Fact(Skip = "#280: paragraph borders are not drawn yet (backlog P0-2, branch p4/q1-borders); the rule under the header is lost")]
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

    [Fact(Skip = "#66: found by this test: Word paragraphs take 한글's 어절 breaking from the template (blank.hwpx breakNonLatinWord, DocxReader leaves HLineBreaking.NonLatin open), so Chinese text without spaces is one word, broken at any character when it overflows: a full stop starts a line, an opening bracket ends one")]
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

    // ───────────────────────── Word: right-to-left documents (#202, #216, #620) ─────────────────────────

    [Fact(Skip = "#202, #185: Word's w:bidi is not read, so an Arabic paragraph that starts with Latin text is laid out left to right (backlog P0-4, branch p4/q1-rtl)")]
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

    [Fact(Skip = "#216: w:bidiVisual is not read, so a right-to-left table keeps its first column on the left (backlog P0-4, branch p4/q1-rtl)")]
    public async Task Issue216_a_right_to_left_table_puts_its_first_column_on_the_right()
    {
        var cells = string.Concat(new[] { "first", "second" }.Select(t => $"<w:tc><w:tcPr><w:tcW w:w=\"3000\" w:type=\"dxa\"/></w:tcPr>{P(R(t))}</w:tc>"));
        var page = Single(await DocxAsync(Docx($"<w:tbl><w:tblPr><w:bidiVisual/><w:tblW w:w=\"6000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid><w:tr>{cells}</w:tr></w:tbl>")));

        var glyphs = Glyphs(page);
        Assert.True(Span(glyphs, "first").Left > Span(glyphs, "second").Right);
    }

    [Fact(Skip = "#620: a note's own number (w:footnoteRef) in right-to-left note text isn't placed between its brackets (backlog P0-4, branch p4/q1-rtl)")]
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

    [Fact(Skip = "#65, #51: 한글 paragraph borders (문단 테두리, an exam's 보기 box) are not drawn yet (backlog P0-2, branch p4/q1-borders)")]
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

    [Fact(Skip = "#1098: a block quote's bar and background (HParaFormat.LeftBar, Fill) are not drawn yet (backlog P0-2, branch p4/q1-borders)")]
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

    /// <summary>The printed pages' words, one string per page.</summary>
    private List<string> PrintSheet(XlsxBuilder book)
    {
        var pdf = SheetPrinter.ToPdf(XlsxReader.Read(Save(book)), new SheetPrintSource("book.xlsx", ""), Token);
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

    /// <summary>A glyph's advance in its run's font and size.</summary>
    private static float Width(Glyph glyph)
    {
        using var font = new SKFont(glyph.Run.Font.Typeface, glyph.Run.Size) { ScaleX = glyph.Run.ScaleX };
        return font.GetGlyphWidths([glyph.Run.Glyphs[glyph.GlyphIndex]])[0];
    }

    /// <summary>From the left of the leftmost glyph to the right of the rightmost of the run that draws <paramref name="word"/>.</summary>
    private static (float Left, float Right) Span(List<Glyph> glyphs, string word)
    {
        var drawn = glyphs.Where(g => g.Run.Text == word).ToList();
        Assert.NotEmpty(drawn);
        return (drawn.Min(g => g.X), drawn.Max(g => g.X + Width(g)));
    }

    private float Advance(string family, char c, float size)
    {
        using var font = new SKFont(_fonts.Font(family).Typeface, size);
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
