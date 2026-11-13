// HWP / HWPX → TXT, Markdown and HTML with the built-in readers and writers (HwpTextConverter): HWP files built in
// code (HwpBuilder), HWPX written by Filee and the hand-made 한글-style package, on every platform.

using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

public class HwpTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Hwp(string folder, params string[] paragraphs)
    {
        var builder = new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart();
        foreach (var paragraph in paragraphs)
            builder.Paragraph(paragraph);
        var path = Path.Combine(folder, "문서.hwp");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private async Task<string> ConvertAsync(string source, string target, Preset? preset = null)
    {
        preset ??= new Preset();
        preset.TargetFormat = target;
        var job = await fx.ConvertAsync([source], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    [Theory]
    [InlineData("hwp", "txt")]
    [InlineData("hwp", "md")]
    [InlineData("hwp", "html")]
    [InlineData("hwpx", "txt")]
    [InlineData("hwpx", "md")]
    [InlineData("hwpx", "html")]
    public void Hwp_text_routes_take_the_built_in_engine_in_the_default_order(string from, string to)
    {
        // Even with every optional engine installed.
        var catalog = new ConverterCatalog(fx.Converters) { Priority = new AppSettings().EnginePriority };
        var planner = catalog.CreatePlanner(["rhwp", "libreoffice", "pandoc", "calibre"]);
        Assert.Equal([HwpTextConverter.EngineId], planner.Plan(from, to)!.Steps.Select(s => s.Converter.Id));
        Assert.True(catalog.StatusOf(fx.Converters.Single(c => c.Id == HwpTextConverter.EngineId)).IsAvailable);
    }

    [Fact]
    public async Task Hwp_becomes_text_markdown_and_html_without_a_native_library()
    {
        var hwp = Hwp(fx.NewFolder(), "한글 문서 파일 형식", "두 번째 문단입니다. 文件转换 Hello");

        var txt = await ConvertAsync(hwp, "txt");
        Assert.Equal("한글 문서 파일 형식\n\n두 번째 문단입니다. 文件转换 Hello\n", Read(txt));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(txt)[..3]); // TXT with a byte order mark, as Unhwp wrote it

        var md = await ConvertAsync(hwp, "md");
        Assert.Equal("한글 문서 파일 형식\n\n두 번째 문단입니다. 文件转换 Hello\n", Read(md));

        var html = Read(await ConvertAsync(hwp, "html"));
        Assert.Contains("<p>한글 문서 파일 형식</p>", html);
        Assert.Contains("<title>문서</title>", html);
        Assert.Contains("lang=\"ko\"", html);
    }

    [Fact]
    public async Task Markdown_keeps_headings_lists_tables_and_pictures()
    {
        var dir = fx.NewFolder();
        EngineFixture.MakeImage(dir, "chart.png", ImageMagick.MagickFormat.Png);
        var source = Path.Combine(dir, "memo.md");
        await File.WriteAllTextAsync(source, "# 회의 메모\n\n## 안건\n\n- 첫째 항목\n- 둘째 항목\n\n1. 번호 하나\n2. 번호 둘\n\n그림 앞 문단.\n\n![](chart.png)\n\n그림 뒤 문단.\n\n| 이름 | 값 |\n|---|---|\n| 가 | 1 |\n",
            TestContext.Current.CancellationToken);
        var hwpx = await ConvertAsync(source, "hwpx");
        var output = fx.NewFolder();
        File.Move(hwpx, Path.Combine(output, "memo.hwpx"));
        hwpx = Path.Combine(output, "memo.hwpx");

        var markdown = Read(await ConvertAsync(hwpx, "md"));
        Assert.DoesNotContain("title:", markdown); // the text starts with the document
        Assert.Contains("# **회의 메모**", markdown);
        Assert.Contains("## **안건**", markdown);
        Assert.Contains("- 첫째 항목\n- 둘째 항목\n", markdown);
        Assert.Contains("1. 번호 하나\n2. 번호 둘\n", markdown);
        Assert.Contains("| 이름 | 값 |\n|---|---|\n| 가 | 1 |", markdown);
        // The picture goes into "memo_files" next to the Markdown.
        var link = Regex.Match(markdown, @"!\[[^\]]*\]\(<([^>]+)>\)").Groups[1].Value;
        Assert.Equal("memo_files/img0001.png", link);
        Assert.True(File.Exists(Path.Combine(output, link)));

        // HTML is one page: the picture is embedded.
        var html = Read(await ConvertAsync(hwpx, "html"));
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.Contains("<table", html);
        Assert.Contains("<h1", html);

        // Text only: no picture, no folder.
        var folders = Directory.GetDirectories(output).Length;
        var textOnly = Read(await ConvertAsync(hwpx, "md", new Preset { Document = { LeaveOutPictures = true } }));
        Assert.DoesNotContain("![", textOnly);
        Assert.Contains("그림 앞 문단.\n\n그림 뒤 문단", textOnly);
        Assert.Equal(folders, Directory.GetDirectories(output).Length);
    }

    [Fact]
    public async Task Text_keeps_list_numbers_cells_and_notes_of_a_hangul_document()
    {
        var hwpx = HwpxReaderTests.HangulStylePackage(fx.NewFolder());
        var text = Read(await ConvertAsync(hwpx, "txt"));

        Assert.Contains("1. 제1장 개요", text);       // the outline number 한글 shows
        Assert.Contains("● 글머리 항목", text);
        Assert.Contains("병합\t세로", text);           // table rows as tab-separated cells
        Assert.Contains("각주 참조[1][2]", text);      // notes collected at the end
        Assert.EndsWith("[1] 각주 내용\n[2] 미주 내용\n", text);
        Assert.DoesNotContain("지운 글", text);        // a tracked deletion is not text
        Assert.Contains("둘째 구역", text);            // every section
    }

    [Fact]
    public void A_text_box_is_text_of_its_own()
    {
        var box = new HTextBox();
        box.Blocks.Add(Paragraph("글상자"));
        var anchor = new HParagraph();
        anchor.Inlines.Add(new HText("앞 글", default));
        anchor.Inlines.Add(box);
        anchor.Inlines.Add(new HText("뒤 글", default));
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { anchor } });

        Assert.Equal("앞 글\n글상자\n뒤 글\n", PlainTextWriter.Write(document).ReplaceLineEndings("\n"));
        Assert.Equal("앞 글\\\n글상자\\\n뒤 글\n", MarkdownWriter.Write(document, _ => null));

        // A box at the end of its paragraph leaves no line break behind.
        anchor.Inlines.RemoveAt(2);
        Assert.Equal("앞 글\\\n글상자\n", MarkdownWriter.Write(document, _ => null));
    }

    [Fact]
    public async Task A_damaged_hwp_fails_with_a_message()
    {
        var hwp = Hwp(fx.NewFolder(), "본문");
        var bytes = File.ReadAllBytes(hwp);
        File.WriteAllBytes(hwp, bytes[..700]); // the compound file cut short

        var job = await fx.ConvertAsync([hwp], new Preset { TargetFormat = "md" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.False(string.IsNullOrEmpty(job.Files.Single().ErrorKey));
    }

    private static HParagraph Paragraph(string text)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(text, default));
        return paragraph;
    }

    // ───────────────────────── Line breaks inside a paragraph (issue #1416) ─────────────────────────

    private static HParagraph Paragraph(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    /// <summary>A paragraph of the lines, a line break (hp:lineBreak in HWPX) between each two.</summary>
    private static HParagraph Lines(params string[] lines)
    {
        var paragraph = Paragraph(lines[0]);
        foreach (var line in lines.Skip(1))
            paragraph.Inlines.AddRange([new HLineBreak(default), new HText(line, default)]);
        return paragraph;
    }

    private static string WriteHwpx(string folder, params HBlock[] blocks)
    {
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        var document = new HDocument();
        document.Sections.Add(section);
        var path = Path.Combine(folder, "code.hwpx");
        HwpxWriter.Write(document, path);
        return path;
    }

    /// <summary>The issue's code: an empty line, indentation, a line that would read as a heading in Markdown.</summary>
    private string Code() =>
        WriteHwpx(fx.NewFolder(), Lines("import a;", "", "class X {", "    int y; // # 1", "# not a heading", "}"), Paragraph("Second para."));

    [Fact]
    public async Task Text_keeps_the_lines_their_indentation_and_empty_lines()
    {
        var text = Read(await ConvertAsync(Code(), "txt"));

        Assert.Equal("import a;\n\nclass X {\n    int y; // # 1\n# not a heading\n}\n\nSecond para.", text.Trim());
    }

    [Fact]
    public async Task Markdown_has_hard_breaks_and_no_line_reads_as_markup()
    {
        var markdown = Read(await ConvertAsync(Code(), "md"));

        Assert.Equal("import a;\\\n\\\nclass X {\\\n    int y; // # 1\\\n\\# not a heading\\\n}\n\nSecond para.", markdown.Trim());
        // Read back, the lines are lines of one paragraph again: no heading.
        var read = MarkdownReader.Read(markdown, fx.NewFolder()).Sections.Single().Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(2, read.Count);
        Assert.All(read, p => Assert.Equal(0, p.HeadingLevel));
        Assert.Equal(5, read[0].Inlines.OfType<HLineBreak>().Count());
    }

    [Fact]
    public async Task Html_has_a_break_for_each_line_and_keeps_the_indentation()
    {
        var html = Read(await ConvertAsync(Code(), "html"));

        Assert.Matches(@"import a;<br ?/>\n?<br ?/>\n?class X \{<br ?/>\n?(    |(&#160;|&nbsp;| ){4})int y; // # 1<br ?/>\n?# not a heading<br ?/>\n?\}</p>", html);
        Assert.DoesNotContain("<h1", html);
    }

    [Fact]
    public async Task Bold_lines_and_table_cells_keep_their_breaks()
    {
        var bold = new HCharFormat(Bold: true);
        var table = new HTable { ColumnCount = 2 };
        foreach (var texts in new string[][] { ["cell a", "cell b"], ["r2"] })
        {
            var row = new HRow();
            foreach (var cellText in new[] { Lines(texts), Paragraph("other") })
            {
                var cell = new HCell();
                cell.Blocks.Add(cellText);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        var hwpx = WriteHwpx(fx.NewFolder(), Paragraph(new HText("Label", bold), new HLineBreak(bold), new HText("value", default), new HLineBreak(default)), table);

        var markdown = Read(await ConvertAsync(hwpx, "md"));
        // The bold closes before the break; the break that ends the paragraph goes.
        Assert.Contains("**Label**\\\nvalue\n", markdown);
        Assert.Contains("| cell a<br>cell b |", markdown);

        var html = Read(await ConvertAsync(hwpx, "html"));
        // A break that ends a paragraph shows no line in a browser, so the HTML may keep it.
        Assert.Matches(@"<(strong|b)>Label</\1><br ?/>\n?value(<br ?/>)?</p>", html);
        Assert.Matches(@"cell a<br ?/?>\n?cell b", html);
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, "a\\\nb")]
    [InlineData(new[] { "a", "", "    b" }, "a\\\n\\\n    b")]
    [InlineData(new[] { "a", "# h", "- l", "+ p", "1. n", "> q", "---", "-", "<div>", ": d", "```" },
        "a\\\n\\# h\\\n\\- l\\\n\\+ p\\\n1\\. n\\\n\\> q\\\n\\---\\\n\\-\\\n\\<div\\>\\\n\\: d\\\n\\`\\`\\`")]
    [InlineData(new[] { "a", "-1", "x - y" }, "a\\\n-1\\\nx - y")]
    [InlineData(new[] { "end", "", "" }, "end")]
    public void Every_line_of_a_markdown_paragraph_stays_a_line(string[] lines, string expected)
    {
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { Lines(lines) } });

        var markdown = MarkdownWriter.Write(document, _ => null);

        Assert.Equal(expected + "\n", markdown);
        // Read back: one plain paragraph, the lines as they were.
        var read = Assert.Single(MarkdownReader.Read(markdown, fx.NewFolder()).Sections.Single().Blocks);
        var paragraph = Assert.IsType<HParagraph>(read);
        Assert.Equal((0, null), (paragraph.HeadingLevel, paragraph.List));
        var text = string.Concat(paragraph.Inlines.Select(i => i switch { HText t => t.Text, HLineBreak => "\n", _ => "" }));
        Assert.Equal(string.Join("\n", lines.Select(l => l.TrimStart())).TrimEnd('\n'), text);
    }

    [Fact]
    public async Task A_document_without_line_breaks_is_read_as_it_is()
    {
        var hwpx = WriteHwpx(fx.NewFolder(), Paragraph("One line."), Paragraph("    Indented line."));

        Assert.Equal("One line.\n\n    Indented line.", Read(await ConvertAsync(hwpx, "txt")).Trim());
        Assert.Equal("One line.\n\n    Indented line.", Read(await ConvertAsync(hwpx, "md")).Trim());
    }
}
