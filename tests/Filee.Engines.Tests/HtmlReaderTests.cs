// The built-in HTML reader (AngleSharp) and XHTML writer: structure, formatting, lists, tables, pictures, links,
// encodings and XHTML quirks, and HTML → HWPX / PDF without Pandoc.

using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class HtmlReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Only engines that ship with the app (or run in-process).</summary>
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre"];

    private HtmlContent Parse(string html, string? folder = null)
    {
        folder ??= fx.NewFolder();
        return HtmlReader.Parse(html, new HtmlReadOptions { BaseFolder = folder, MediaFolder = Path.Combine(folder, "media") });
    }

    private static List<HParagraph> Paragraphs(HtmlContent content) => content.Blocks.OfType<HParagraph>().ToList();

    private static string Text(HParagraph paragraph) => HDocumentWalker.Text(paragraph.Inlines);

    private static HText Run(HtmlContent content, string text) =>
        content.Blocks.SelectMany(b => HDocumentWalker.Inlines([b])).OfType<HText>().First(t => t.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Headings_paragraphs_and_inline_formatting()
    {
        var content = Parse("""
            <html><head><title>제목 </title><style>.x{font-weight:bold} p.c{text-align:center} body{color:red}</style>
            <script>document.write('no')</script></head>
            <body>
            <h1>큰   제목</h1>
            <h3>작은 제목</h3>
            <p>일반 <b>굵게</b> <i>기울임</i> <u>밑줄</u> <del>취소</del> H<sub>2</sub>O x<sup>2</sup> <code>code</code> <mark>표시</mark></p>
            <p><span style="color:#ff0000; font-size:20pt; font-style:italic">빨강</span> <span class="x">클래스</span></p>
            <p class="c">가운데</p>
            <p style="text-align:right">오른쪽</p>
            <nav>메뉴</nav><div style="display:none">숨김</div>
            </body></html>
            """);

        Assert.Equal("제목", content.Title);
        var paragraphs = Paragraphs(content);
        Assert.Equal((1, "큰 제목"), (paragraphs[0].HeadingLevel, Text(paragraphs[0])));
        Assert.Equal(3, paragraphs[1].HeadingLevel);
        Assert.True(Run(content, "굵게").Format.Bold);
        Assert.True(Run(content, "기울임").Format.Italic);
        Assert.True(Run(content, "밑줄").Format.Underline);
        Assert.True(Run(content, "취소").Format.Strike);
        Assert.True(Run(content, "2").Format.Subscript);
        Assert.Equal(HtmlReader.CodeShade, Run(content, "code").Format.Shade);
        Assert.Equal(HtmlReader.MarkShade, Run(content, "표시").Format.Shade);
        var red = Run(content, "빨강").Format;
        Assert.Equal(("#FF0000", 2000, true), (red.Color, red.Size, red.Italic));
        Assert.True(Run(content, "클래스").Format.Bold); // style sheet class rule
        Assert.Null(Run(content, "일반").Format.Color); // body colour from the style sheet is not copied onto runs
        Assert.Equal(HAlign.Center, paragraphs.Single(p => Text(p) == "가운데").Format.Align);
        Assert.Equal(HAlign.Right, paragraphs.Single(p => Text(p) == "오른쪽").Format.Align);
        var all = string.Join("\n", paragraphs.Select(Text));
        Assert.DoesNotContain("메뉴", all);
        Assert.DoesNotContain("숨김", all);
        Assert.DoesNotContain("document.write", all);
    }

    [Fact]
    public void Style_sheet_colours_apply_to_classes_and_elements_but_not_to_the_whole_page()
    {
        // #1237: a page styled from <style> came out without its colours.
        var content = Parse("""
            <html><head><style>
            @charset "utf-8";
            @import url("https://fonts.example/a;b.css");
            .first { font-weight: bold }
            * { color: #111; background: #eee }
            html, body { color: red; background: #081b29 }
            h1 { color: #c00 }
            .note, p.tip { background-color: #ffe; color: rgb(0, 0, 255) }
            th { background: #ddd }
            @media print { .first { color: green } }
            </style></head><body>
            <h1>제목</h1><p class="first">첫째</p><p>본문</p><p class="note">참고</p>
            <table><tr><th>머리</th></tr><tr><td>칸</td></tr></table>
            </body></html>
            """);

        Assert.True(Run(content, "첫째").Format.Bold); // the first rule after @import / @charset is kept
        Assert.Null(Run(content, "첫째").Format.Color); // inside @media: not applied
        Assert.Equal("#CC0000", Run(content, "제목").Format.Color);
        var note = Run(content, "참고").Format;
        Assert.Equal(("#0000FF", "#FFFFEE"), (note.Color, note.Shade));
        var plain = Run(content, "본문").Format; // * / html / body rules stay out of every run
        Assert.Null(plain.Color);
        Assert.Null(plain.Shade);
        var table = content.Blocks.OfType<HTable>().Single();
        Assert.Equal("#DDDDDD", table.Rows[0].Cells[0].Fill);
        Assert.Null(table.Rows[1].Cells[0].Fill);
        // A style attribute still wins over the style sheet.
        Assert.Equal("#00FF00", Run(Parse("<style>p{color:#c00}</style><p style=\"color:#0f0\">x</p>"), "x").Format.Color);
    }

    [Fact]
    public void Thousands_of_statement_at_rules_are_skipped_in_linear_time()
    {
        // Cutting the text after every @import made this quadratic: 80,000 lines took minutes.
        var sheet = string.Concat(Enumerable.Repeat("@import \"a.css\";\n", 80_000)) + ".x{font-weight:bold}";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var content = Parse($"<style>{sheet}</style><p class=\"x\">굵게</p>");
        timer.Stop();

        Assert.True(Run(content, "굵게").Format.Bold);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"took {timer.Elapsed}");
    }

    [Fact]
    public void A_style_background_replaces_the_shade_of_pre_for_dark_code_themes()
    {
        var content = Parse("""
            <style>pre { background: #282c34; color: #f8f8f2 } .plain { color: #123456 }</style>
            <pre>어두운 코드</pre><pre class="plain">기본 코드</pre><pre><code>안쪽 코드</code></pre>
            """);

        var dark = Run(content, "어두운").Format;
        Assert.Equal(("#282C34", "#F8F8F2"), (dark.Shade, dark.Color));
        Assert.Equal(HtmlReader.CodeShade, Run(content, "기본").Format.Shade); // no background in the style sheet
        Assert.Equal("#123456", Run(content, "기본").Format.Color);
    }

    [Fact]
    public void Text_that_would_vanish_on_its_shade_or_on_the_page_turns_black_or_white()
    {
        // The page background of #1237 is not kept, so rules written for it must not leave unreadable text.
        var content = Parse("""
            <style>
            body { color: #fff }
            .card { background: #111827 }
            .hero { background: linear-gradient(#081b29, #0ef); color: #fff }
            .lime { color: #0f0 }
            .badge { background: #111827; color: #f9fafb }
            </style>
            <p class="card">카드</p><p class="hero">히어로</p><p class="lime">라임</p><p class="badge">배지</p>
            """);

        var card = Run(content, "카드").Format; // black default text on a near-black shade: the shade stays
        Assert.Equal(("#111827", "#FFFFFF"), (card.Shade, card.Color));
        var hero = Run(content, "히어로").Format; // white text on the white page
        Assert.Null(hero.Shade);
        Assert.Equal("#000000", hero.Color);
        Assert.Equal("#00FF00", Run(content, "라임").Format.Color); // contrast about 1.37: left as written
        Assert.Equal("#F9FAFB", Run(content, "배지").Format.Color); // readable pairs are untouched
    }

    [Fact]
    public void Colours_with_an_alpha_channel_are_blended_over_white_and_transparent_ones_are_dropped()
    {
        var content = Parse("""
            <style>
            .glass { background: rgba(0, 0, 0, 0.5) }
            .reset { background: rgba(0, 0, 0, 0); color: rgba(0, 0, 0, 0) }
            .short { background: #0000; color: #00000000 }
            .hex { background: #00000080; color: rgb(255 255 255 / 100%) }
            .slash { background: rgb(0 0 0 / 50%) }
            </style>
            <p class="glass">유리</p><p class="reset">초기화</p><p class="short">짧게</p><p class="hex">헥스</p><p class="slash">슬래시</p>
            """);

        Assert.Equal("#808080", Run(content, "유리").Format.Shade);
        var reset = Run(content, "초기화").Format;
        Assert.Null(reset.Shade);
        Assert.Null(reset.Color);
        var tiny = Run(content, "짧게").Format;
        Assert.Null(tiny.Shade);
        Assert.Null(tiny.Color);
        var hex = Run(content, "헥스").Format;
        Assert.Equal(("#7F7F7F", "#FFFFFF"), (hex.Shade, hex.Color));
        Assert.Equal("#808080", Run(content, "슬래시").Format.Shade);
    }

    [Fact]
    public void Whitespace_collapses_and_line_breaks_stay()
    {
        var paragraphs = Paragraphs(Parse("<p>  하나   \n  둘<br>셋<br></p><p><br></p><p></p><div>\n</div><pre>  들여쓴\n\n코드</pre>"));

        Assert.Equal(["하나 둘", "셋"], paragraphs[0].Inlines.OfType<HText>().Select(t => t.Text));
        Assert.Single(paragraphs[0].Inlines.OfType<HLineBreak>()); // the trailing <br> is not shown
        Assert.Empty(paragraphs[1].Inlines); // <p><br></p>: an intended empty line
        var code = paragraphs.Skip(2).ToList();
        Assert.Equal(["  들여쓴", "", "코드"], code.Select(c => string.Concat(c.Inlines.OfType<HText>().Select(t => t.Text))));
        Assert.All(code.SelectMany(c => c.Inlines.OfType<HText>()), t => Assert.Equal(HtmlReader.CodeShade, t.Format.Shade));
    }

    [Fact]
    public void Nested_lists_keep_levels_start_numbers_and_types()
    {
        var paragraphs = Paragraphs(Parse("""
            <ol start="3" type="a">
              <li>셋째<ul><li>안쪽</li><li>안쪽 둘</li></ul>이어지는 글</li>
              <li><p>넷째</p><p>둘째 문단</p></li>
            </ol>
            <ul style="list-style:none"><li>표시 없음</li></ul>
            """));

        var third = paragraphs.Single(p => Text(p) == "셋째");
        Assert.Equal((0, true), (third.List!.Level, third.List.Numbered));
        Assert.Equal(("LATIN_SMALL", 3, false), (third.List.Numbering.Levels[0].Format, third.List.Numbering.Levels[0].Start, third.List.Numbering.Levels[0].Bullet));
        var inner = paragraphs.Single(p => Text(p) == "안쪽");
        Assert.Equal((1, true), (inner.List!.Level, inner.List.Numbering.Levels[1].Bullet));
        Assert.Same(inner.List.Numbering, paragraphs.Single(p => Text(p) == "안쪽 둘").List!.Numbering);
        var continued = paragraphs.Single(p => Text(p) == "이어지는 글");
        Assert.Equal((0, false), (continued.List!.Level, continued.List.Numbered));
        Assert.Same(third.List.Numbering, continued.List.Numbering);
        Assert.True(paragraphs.Single(p => Text(p) == "넷째").List!.Numbered);
        Assert.False(paragraphs.Single(p => Text(p) == "둘째 문단").List!.Numbered);
        var unmarked = paragraphs.Single(p => Text(p) == "표시 없음");
        Assert.Null(unmarked.List);
        Assert.True(unmarked.Format.Left > 0);
    }

    [Fact]
    public void Tables_keep_spans_header_rows_and_borders()
    {
        var content = Parse("""
            <table border="0">
              <caption>표 제목</caption>
              <thead><tr><th>이름</th><th colspan="2">값</th></tr></thead>
              <tbody>
                <tr><td rowspan="2" bgcolor="#eeeeee">병합</td><td>1</td><td>2</td></tr>
                <tr><td>3</td><td>4</td></tr>
              </tbody>
            </table>
            <table><tr><td style="width:25%">a</td><td style="width:75%">b</td></tr></table>
            """);

        var tables = content.Blocks.OfType<HTable>().ToList();
        var table = tables[0];
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal([true, false, false], table.Rows.Select(r => r.Header));
        Assert.True(((HText)((HParagraph)table.Rows[0].Cells[0].Blocks[0]).Inlines[0]).Format.Bold);
        Assert.Equal(2, table.Rows[0].Cells[1].ColSpan);
        Assert.Equal((2, "#EEEEEE"), (table.Rows[1].Cells[0].RowSpan, table.Rows[1].Cells[0].Fill));
        Assert.Equal(HBorders.Empty, table.Borders);
        Assert.Equal("표 제목", Text((HParagraph)table.Caption[0]));
        Assert.Null(tables[1].Borders); // default: thin grid
        Assert.Equal([0.25, 0.75], tables[1].RelativeWidths!);
    }

    [Fact]
    public void Pictures_from_files_and_data_uris_links_and_anchors()
    {
        var folder = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "img"));
        File.WriteAllBytes(Path.Combine(folder, "img", "로고 1.png"), EbookBuilders.Png(40, 20, MagickColors.Teal));
        var data = Convert.ToBase64String(EbookBuilders.Png(10, 10, MagickColors.Red));
        var content = Parse($"""
            <p><a href="#part2">둘째로</a> <a href="https://example.com/x?a=1">웹</a> <a name="unused">아무도</a></p>
            <p><img src="img/%EB%A1%9C%EA%B3%A0%201.png" width="200"> <img src="data:image/png;base64,{data}"> <img src="https://example.com/remote.png"></p>
            <h2 id="part2">둘째 부분</h2>
            """, folder);
        var document = new HDocument();
        document.Sections.Add(new HSection());
        document.Sections[0].Blocks.AddRange(content.Blocks);
        HtmlReader.PruneBookmarks(document);

        var inlines = document.Sections[0].Blocks.SelectMany(b => HDocumentWalker.Inlines([b])).ToList();
        var links = inlines.OfType<HLink>().ToList();
        Assert.Equal(["#part2", "https://example.com/x?a=1"], links.Select(l => l.Target));
        Assert.Equal(["part2"], inlines.OfType<HBookmark>().Select(b => b.Name)); // "unused" is pruned
        var images = inlines.OfType<HImage>().ToList();
        Assert.Equal(2, images.Count); // the remote picture is skipped
        Assert.Equal(Path.Combine(folder, "img", "로고 1.png"), images[0].Path);
        Assert.Equal((int)(200 * HwpxUnits.PerPixel), images[0].Width);
        Assert.True(File.Exists(images[1].Path));
        Assert.StartsWith(Path.Combine(folder, "media"), images[1].Path);
    }

    [Fact]
    public void Css_page_breaks_start_new_pages()
    {
        var paragraphs = Paragraphs(Parse("""
            <p style="page-break-before:always">첫</p>
            <p>둘</p>
            <div style="break-after: page"><p>셋</p></div>
            <p>넷</p>
            <mbp:pagebreak/>
            <p>다섯</p>
            """));

        Assert.Equal([false, false, false, true, true], paragraphs.Select(p => p.PageBreakBefore)); // none before the first paragraph
    }

    [Fact]
    public void Encodings_come_from_the_bom_the_xml_declaration_or_meta_charset()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string Korean = "한글 문서";
        const string Russian = "Русский текст";

        Assert.Contains(Korean, HtmlReader.Decode(Encoding.GetEncoding(949).GetBytes($"<html><head><meta charset=\"euc-kr\"></head><body>{Korean}</body></html>")));
        Assert.Contains(Korean, HtmlReader.Decode(Encoding.GetEncoding(949).GetBytes($"<meta http-equiv=\"Content-Type\" content=\"text/html; charset=ks_c_5601-1987\"><p>{Korean}</p>")));
        Assert.Contains(Russian, HtmlReader.Decode(Encoding.GetEncoding(1251).GetBytes($"<?xml version=\"1.0\" encoding=\"windows-1251\"?><html><body>{Russian}</body></html>")));
        Assert.Contains(Korean, HtmlReader.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes($"<p>{Korean}</p>")]));
        Assert.Contains(Korean, HtmlReader.Decode(Encoding.UTF8.GetBytes($"<p>{Korean}</p>"))); // undeclared UTF-8
    }

    [Fact]
    public void Xhtml_self_closed_elements_do_not_swallow_the_page()
    {
        var content = HtmlReader.Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head>
            <body><div id="a"/><p>첫 문단</p><a id="b"/><p>둘째 문단</p><br/><hr/></body></html>
            """, new HtmlReadOptions { BaseFolder = fx.NewFolder(), MediaFolder = fx.NewFolder() }, xhtml: true);

        var paragraphs = Paragraphs(content);
        Assert.Equal("첫 문단", Text(paragraphs[0]));
        Assert.Equal("둘째 문단", Text(paragraphs[1]));
        Assert.DoesNotContain(paragraphs[1].Inlines, i => i is HLink);
        Assert.IsType<HShape>(paragraphs[^1].Inlines.Single()); // <hr/> is a short line
    }

    [Fact]
    public void Absurdly_deep_html_is_read_as_text_without_exhausting_the_stack()
    {
        var html = string.Concat(Enumerable.Repeat("<div><span>", 5000)) + "깊은 글" + string.Concat(Enumerable.Repeat("</span></div>", 5000));

        Assert.Contains(Paragraphs(Parse(html)), p => Text(p) == "깊은 글");
    }

    [Fact]
    public void Xhtml_writer_output_is_well_formed_and_links_across_chapters()
    {
        var content = Parse("""
            <h1>하나</h1><p>본문 <a href="#target">링크</a> <b>굵게</b> &amp; &lt;기호&gt;</p>
            <ol start="2"><li>둘<ul><li>안</li></ul></li><li>셋</li></ol>
            <table><tr><th>A</th><th>B</th></tr><tr><td colspan="2">합침</td></tr></table>
            <h1>둘</h1><p id="target">목표</p><pre>code  line</pre>
            """);
        var document = new HDocument();
        document.Sections.Add(new HSection());
        document.Sections[0].Blocks.AddRange(content.Blocks);
        ((HParagraph)document.Sections[0].Blocks[1]).Inlines.Add(new HNote(false) { Blocks = { new HParagraph { Inlines = { new HText("각주", default) } } } });

        var chapters = XhtmlWriter.Write(document, new XhtmlOptions { SplitChapters = true, ImageSource = _ => null, Epub = true });

        Assert.Equal(2, chapters.Count);
        Assert.Equal(["하나", "둘"], chapters.Select(c => c.Title));
        foreach (var chapter in chapters)
            XDocument.Parse(XhtmlWriter.Page(chapter.Title!, chapter.Body, "ko", "style.css", epub: true)); // throws if malformed
        Assert.Contains("href=\"ch002.xhtml#target\"", chapters[0].Body);
        Assert.Contains("id=\"target\"", chapters[1].Body);
        Assert.Contains("<ol start=\"2\">", chapters[0].Body);
        Assert.Matches("<li>둘<ul>\\s*<li>안</li>\\s*</ul>\\s*</li>", chapters[0].Body);
        Assert.Contains("colspan=\"2\"", chapters[0].Body);
        Assert.Contains("epub:type=\"footnote\"", chapters[0].Body);
        Assert.Contains("<pre><code>code  line</code></pre>", chapters[1].Body);
    }

    [Fact]
    public void Html_to_pdf_needs_no_optional_engine()
    {
        var planner = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner(["rhwp"]);

        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("html", "hwpx")!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("html", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal("ebook", Assert.Single(planner.Plan("html", "txt")!.Steps).Converter.Id);
        Assert.Equal("ebook", Assert.Single(planner.Plan("html", "epub")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Html_becomes_hwpx_and_pdf_with_built_in_engines()
    {
        var dir = fx.NewFolder();
        File.WriteAllBytes(Path.Combine(dir, "chart.png"), EbookBuilders.Png(300, 150, MagickColors.MediumPurple));
        var html = Path.Combine(dir, "보고서.html");
        await File.WriteAllTextAsync(html, """
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>보고서</title></head><body>
            <h1>분기 보고서</h1>
            <p>매출이 <strong>12%</strong> 늘었습니다.</p>
            <ul><li>서울</li><li>부산</li></ul>
            <table><tr><th>지역</th><th>매출</th></tr><tr><td>서울</td><td>100</td></tr></table>
            <p><img src="chart.png" alt="차트"></p>
            <p style="page-break-before:always">둘째 쪽</p>
            </body></html>
            """, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([html], new Preset { TargetFormat = "hwpx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var hwpx = job.Outputs.Single();
        ValidPackage(hwpx, expectImages: 1);
        {
            var text = HwpxAssert.ReadBackText(hwpx);
            foreach (var expected in new[] { "분기 보고서", "12%", "서울", "부산", "매출", "둘째 쪽" })
                Assert.Contains(expected, text);
        }
        Assert.Single(Xml(hwpx).Descendants(Hp + "tbl"));

        var pages = RenderWithRhwp(fx, hwpx, "hwpx-from-html");
        if (pages is not null)
            Assert.Equal(2, pages);
    }
}
