// Markdown without optional engines (Markdig, in-process), the progress estimate of silent engines and the LibreOffice
// profile warm-up.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

public class MarkdownTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sample = """
        ---
        title: 회의록
        ---

        # 주간 회의

        오늘 결정한 것: **배포**, *문서 정리*, `v1.0.1`.

        | 항목 | 담당 |
        |------|------|
        | 설치 | 민지 |

        ![그림](chart.png)

        - [x] 끝낸 일
        - [ ] 남은 일
        """;

    /// <summary>Only engines that ship with the app (or run in-process): no Pandoc or LibreOffice.</summary>
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }
            .CreatePlanner(assumeInstalled);

    private async Task<string> WriteSampleAsync(string dir)
    {
        using (var chart = new MagickImage(MagickColors.Teal, 160, 90))
            chart.Write(Path.Combine(dir, "chart.png"));
        var md = Path.Combine(dir, "회의록.md");
        await File.WriteAllTextAsync(md, Sample, TestContext.Current.CancellationToken);
        return md;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Markdown_routes_need_no_optional_engine()
    {
        var planner = BuiltInPlanner();

        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "txt")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("md", "hwpx")!.Steps).Converter.Id);
        // PDF: the built-in HWPX writer, then rhwp (bundled with the installer).
        Assert.Equal(["hwpx-writer", "rhwp"], BuiltInPlanner("rhwp").Plan("md", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public void Built_in_markdown_wins_over_pandoc_for_html_and_text()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc"]);

        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "txt")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Markdown_to_html_and_text()
    {
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var html = await File.ReadAllTextAsync(await ConvertAsync(md, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("<h1", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<strong>배포</strong>", html);
        Assert.Contains("src=\"chart.png\"", html); // next to the Markdown file the relative path still works
        Assert.DoesNotContain("title: 회의록", html);

        var text = await File.ReadAllTextAsync(await ConvertAsync(md, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("주간 회의", text);
        Assert.Contains("배포", text);
        Assert.DoesNotContain("**", text);
    }

    [Fact]
    public async Task Html_written_elsewhere_points_images_at_the_original_files()
    {
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var html = MarkdownConverter.ToHtml(Sample, md, Path.Combine(fx.NewFolder(), "step.html"));

        Assert.Contains($"src=\"{new Uri(Path.Combine(dir, "chart.png")).AbsoluteUri}\"", html);
    }

    [Fact]
    public async Task Markdown_to_hwpx_keeps_structure()
    {
        var dir = fx.NewFolder();
        var hwpx = await ConvertAsync(await WriteSampleAsync(dir), "hwpx");

        HwpxAssert.ValidPackage(hwpx, expectImages: 1);
        var text = HwpxAssert.ReadBackText(hwpx);
        foreach (var expected in new[] { "주간 회의", "배포", "v1.0.1", "설치", "민지", "☑ 끝낸 일", "☐ 남은 일" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("title:", text);
        Assert.Single(HwpxAssert.Xml(hwpx).Descendants(HwpxAssert.Hp + "tbl"));
    }

    // ───────────────────────── Block quotes (issue #1098) ──────────

    private const string QuoteLine = "本文档用于统一记录和跟踪运维过程中遇到的问题及解决方案。";

    private static List<HParagraph> ReadParagraphs(string markdown, string folder) =>
        [.. MarkdownReader.Read(markdown, folder).Sections.Single().Blocks.OfType<HParagraph>()];

    private static string Text(HParagraph paragraph) => string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text));

    /// <summary>The ways a quote can open a file: all of them were read as quotes, an indent nobody could see on the page.</summary>
    [Theory]
    [InlineData("> {0}\n\nAfter", false)]
    [InlineData(" > {0}\n\nAfter", false)] // as in the issue
    [InlineData("> {0}\r\n\r\nAfter", false)]
    [InlineData("> {0}\n\nAfter", true)]
    [InlineData("---\ntitle: x\n---\n> {0}\n\nAfter", false)]
    [InlineData("---\ntitle: x\n---\n\n> {0}\n\nAfter", true)]
    public async Task A_block_quote_has_a_bar_and_a_background_wherever_it_starts(string template, bool byteOrderMark)
    {
        var path = Path.Combine(fx.NewFolder(), "quote.md");
        await File.WriteAllTextAsync(path, string.Format(CultureInfo.InvariantCulture, template, QuoteLine),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: byteOrderMark), TestContext.Current.CancellationToken);
        var markdown = DocumentReaders.DecodeText(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        var paragraphs = ReadParagraphs(markdown, fx.NewFolder());

        Assert.Equal([QuoteLine, "After"], paragraphs.Select(Text));
        Assert.Equal(2000, paragraphs[0].Format.Left);
        Assert.NotNull(paragraphs[0].Format.LeftBar);
        Assert.NotNull(paragraphs[0].Format.Fill);
        Assert.True(paragraphs[1].Format.IsEmpty);
    }

    [Fact]
    public void Nested_quotes_indent_per_level_and_only_quotes_get_a_bar()
    {
        var paragraphs = ReadParagraphs("> one\n>\n> > two\n> >\n> > > three\n\nTerm\n: Definition\n\nAfter", fx.NewFolder());

        Assert.Equal(["one", "two", "three", "Term", "Definition", "After"], paragraphs.Select(Text));
        Assert.Equal([2000, 4000, 6000], paragraphs.Take(3).Select(p => p.Format.Left!.Value));
        Assert.All(paragraphs.Take(3), p => Assert.NotNull(p.Format.LeftBar));
        Assert.All(paragraphs.Skip(3), p => Assert.Null(p.Format.LeftBar)); // a definition is indented too, but no quote
    }

    [Fact]
    public async Task A_block_quote_is_a_paragraph_border_in_hwpx()
    {
        var input = Path.Combine(fx.NewFolder(), "quote.md");
        await File.WriteAllTextAsync(input, $"> {QuoteLine}\n\nAfter", TestContext.Current.CancellationToken);

        var hwpx = await ConvertAsync(input, "hwpx");

        HwpxAssert.ValidPackage(hwpx);
        var head = HwpxAssert.Xml(hwpx, "Contents/header.xml");
        var paragraphs = HwpxAssert.Xml(hwpx).Elements(HwpxAssert.Hp + "p").ToList();
        XElement Border(XElement paragraph)
        {
            var shape = head.Descendants(HwpxAssert.Hh + "paraPr").Single(p => (string?)p.Attribute("id") == (string?)paragraph.Attribute("paraPrIDRef"));
            var id = (string?)shape.Element(HwpxAssert.Hh + "border")!.Attribute("borderFillIDRef");
            return head.Descendants(HwpxAssert.Hh + "borderFill").Single(b => (string?)b.Attribute("id") == id);
        }
        var quote = Border(paragraphs[0]);
        Assert.Equal("SOLID", (string?)quote.Element(HwpxAssert.Hh + "leftBorder")!.Attribute("type"));
        Assert.Equal("NONE", (string?)quote.Element(HwpxAssert.Hh + "topBorder")!.Attribute("type"));
        Assert.NotEqual("none", (string?)quote.Descendants(HwpxAssert.Hc + "winBrush").Single().Attribute("faceColor"));
        Assert.NotEqual("SOLID", (string?)Border(paragraphs[1]).Element(HwpxAssert.Hh + "leftBorder")!.Attribute("type"));
    }

    [Fact]
    public void A_block_quote_is_a_paragraph_border_in_docx()
    {
        var output = Path.Combine(fx.NewFolder(), "quote.docx");

        DocxWriter.Write(MarkdownReader.Read($"> {QuoteLine}\n\nAfter", fx.NewFolder()), output);

        using var zip = ZipFile.OpenRead(output);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = XDocument.Load(zip.GetEntry("word/document.xml")!.Open()).Descendants(w + "p").ToList();
        var properties = body[0].Element(w + "pPr")!;
        Assert.Equal("single", (string?)properties.Element(w + "pBdr")!.Element(w + "left")!.Attribute(w + "val"));
        // CT_PPr's order: the border and the shading come before the indent, or Word calls the file damaged.
        var names = properties.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("pBdr") < names.IndexOf("shd") && names.IndexOf("shd") < names.IndexOf("ind"), string.Join(", ", names));
        Assert.Null(body[1].Element(w + "pPr")?.Element(w + "pBdr"));
    }

    /// <summary>The issue itself: what rhwp puts on the page, a grey bar taller than a line beside the quote's background.</summary>
    [Fact]
    public async Task A_block_quote_is_drawn_in_the_pdf()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var input = Path.Combine(fx.NewFolder(), "quote.md");
        await File.WriteAllTextAsync(input, $"# Title\n\n > {QuoteLine}{QuoteLine}{QuoteLine}{QuoteLine}\n\nAfter", TestContext.Current.CancellationToken);

        var png = await ConvertAsync(await ConvertAsync(input, "pdf"), "png");

        using var page = new MagickImage(png);
        using var pixels = page.GetPixels();
        var values = pixels.ToByteArray(PixelMapping.RGB)!;
        var width = (int)page.Width;
        var height = (int)page.Height;
        bool Near(int x, int y, byte r, byte g, byte b, int tolerance)
        {
            var at = (y * width + x) * 3;
            return Math.Abs(values[at] - r) <= tolerance && Math.Abs(values[at + 1] - g) <= tolerance && Math.Abs(values[at + 2] - b) <= tolerance;
        }
        var background = 0;
        var tallestBar = 0;
        for (var x = 0; x < width; x++)
        {
            var run = 0;
            for (var y = 0; y < height; y++)
            {
                if (Near(x, y, 0xF7, 0xF7, 0xF9, 1))
                    background++;
                run = Near(x, y, 0xB8, 0xB8, 0xC0, 24) ? run + 1 : 0;
                tallestBar = Math.Max(tallestBar, run);
            }
        }
        Assert.True(background > 1000, $"{background} background pixels");
        Assert.True(tallestBar > 30, $"the tallest bar is {tallestBar} pixels");
    }

    [Fact]
    public void A_code_block_is_one_shaded_box_in_a_monospace_font()
    {
        // Issue #406: each line used to be a paragraph with shaded text, a strip per line with gaps and none on empty lines.
        var document = Filee.Engines.Hwp.Hwpx.MarkdownReader.Read("Before\n\n```\nint main()\n\n    return 0;\n```\n\nAfter", fx.NewFolder());

        var blocks = document.Sections.Single().Blocks;
        var box = Assert.IsType<Filee.Engines.Hwp.Hwpx.HTable>(blocks[1]);
        Assert.Equal(3, box.Rows.Count);
        Assert.All(box.Rows, r => Assert.Equal("#F1F0F5", Assert.Single(r.Cells).Fill));
        var texts = box.Rows.SelectMany(r => r.Cells[0].Blocks.OfType<Filee.Engines.Hwp.Hwpx.HParagraph>())
            .SelectMany(p => p.Inlines.OfType<Filee.Engines.Hwp.Hwpx.HText>()).ToList();
        Assert.Equal(["int main()", "    return 0;"], texts.Select(t => t.Text));
        Assert.All(texts, t => Assert.Null(t.Format.Shade));
        Assert.All(texts, t => Assert.False(string.IsNullOrEmpty(t.Format.Font)));
        Assert.Equal(box.Rows[0].Cells[0].CellMargin!.Value.Top, box.Rows[2].Cells[0].CellMargin!.Value.Bottom);
    }

    [Fact]
    public async Task Markdown_to_pdf_with_built_in_engines()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var dir = fx.NewFolder();
        var md = await WriteSampleAsync(dir);

        var pdf = await ConvertAsync(md, "pdf");

        Assert.True(new FileInfo(pdf).Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken))[..4]));
    }

    [Fact]
    public void Progress_estimate_rises_towards_the_bound_and_never_reaches_it()
    {
        var values = new[] { 0, 0.5, 1, 2, 4, 8, 30 }.Select(t => ProgressEstimate.Value(t, typical: 2, from: 0.1, to: 0.9)).ToList();

        Assert.Equal(0.1, values[0], 6);
        Assert.True(values.Zip(values.Skip(1)).All(p => p.Second > p.First));
        Assert.True(values[^1] < 0.9);
        Assert.InRange(ProgressEstimate.Value(2, typical: 2, from: 0.1, to: 0.9), 0.6, 0.62); // 1 - 1/e of the way
    }

    [Fact]
    public async Task Progress_estimate_reports_while_running_and_stops_when_disposed()
    {
        var reports = new List<double>();
        var sink = new SyncProgress(v => { lock (reports) reports.Add(v); });
        int Count()
        {
            lock (reports)
                return reports.Count;
        }

        using (ProgressEstimate.Start(sink, TimeSpan.FromSeconds(1)))
        {
            // Timer callbacks run late on a busy machine (CI runners): wait for two reports, up to 10 s.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count() < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        // A callback that was already running when the estimate was disposed may still report once.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var count = Count();
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(count >= 2, $"only {count} reports");
        Assert.Equal(count, Count());
        lock (reports)
            Assert.All(reports, v => Assert.InRange(v, 0.1, 0.9));
    }

    [Fact]
    public async Task LibreOffice_warm_up_prepares_every_profile_once()
    {
        var env = new EngineEnvironment(Path.Combine(fx.NewFolder(), "data"));
        var libreOffice = new LibreOfficeConverter(env);
        Assert.SkipUnless(libreOffice.GetStatus().IsAvailable, "LibreOffice not found");

        Assert.Equal(2, await libreOffice.WarmUpAsync(TestContext.Current.CancellationToken));
        Assert.All(Directory.GetDirectories(Path.Combine(env.DataDirectory, "libreoffice-profiles")),
            profile => Assert.True(Directory.Exists(Path.Combine(profile, "user"))));
        Assert.Equal(0, await libreOffice.WarmUpAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // ───────────────────────── Logs and script source as text (issues #254, #273, #284) ─────────────────────────

    [Theory]
    [InlineData("log")]
    [InlineData("py")]
    [InlineData("pac")]
    [InlineData("bat")]
    [InlineData("command")]
    public async Task Logs_and_script_source_become_utf8_txt_with_the_text_unchanged(string extension)
    {
        var input = Path.Combine(fx.NewFolder(), "source." + extension);
        const string Content = "# Local source text\nprint('مرحبا 中文 한국어')\n<not markup> & \"quoted\" **not bold**\n";
        await File.WriteAllTextAsync(input, Content, TestContext.Current.CancellationToken);
        Assert.Equal("txt", FormatRegistry.Detect(input)?.Id);

        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "txt" });

        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(".txt", Path.GetExtension(output));
        Assert.Equal(Content, await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("py")]
    [InlineData("bat")]
    [InlineData("command")]
    public async Task Script_source_is_read_and_never_run(string extension)
    {
        var folder = fx.NewFolder();
        var marker = Path.Combine(folder, "executed.txt").Replace('\\', '/');
        var input = Path.Combine(folder, "script." + extension);
        var content = extension switch
        {
            "py" => $"from pathlib import Path\nPath(r'{marker}').write_text('executed')\n",
            "bat" => $"@echo off\r\necho executed>\"{marker}\"\r\n",
            _ => $"#!/bin/sh\nprintf executed > '{marker}'\n",
        };
        await File.WriteAllTextAsync(input, content, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "txt" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(content, await File.ReadAllTextAsync(Assert.Single(job.Outputs), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_legacy_korean_text_file_becomes_utf8()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var input = Path.Combine(fx.NewFolder(), "memo.txt");
        await File.WriteAllBytesAsync(input, System.Text.Encoding.GetEncoding(949).GetBytes("한글 메모\n"), TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "txt" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("한글 메모\n", await File.ReadAllTextAsync(Assert.Single(job.Outputs), TestContext.Current.CancellationToken));
    }

    // ───────────────────────── Table column alignment ──────────

    /// <summary>A left, a centred, a right and a plain column; the last row is short.</summary>
    private const string AlignedTable = "| Left | Centre | Right | Plain |\n|:-----|:------:|------:|-------|\n| a | b | c | d |\n| e | f |\n";

    /// <summary>The alignment of each cell with text, row by row (empty cells are left out).</summary>
    private static HAlign?[][] CellAlignments(HDocument document) =>
        [.. document.Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single().Rows
            .Select(row => row.Cells.Select(c => c.Blocks.OfType<HParagraph>().FirstOrDefault(p => p.Inlines.Count > 0)).OfType<HParagraph>()
                .Select(p => p.Format.Align).ToArray())];

    [Fact]
    public void A_pipe_tables_column_alignment_aligns_every_cell_of_the_column()
    {
        var alignments = CellAlignments(MarkdownReader.Read(AlignedTable, fx.NewFolder()));

        Assert.Equal([HAlign.Left, HAlign.Center, HAlign.Right, null], alignments[0]); // the header row too
        Assert.Equal([HAlign.Left, HAlign.Center, HAlign.Right, null], alignments[1]);
        Assert.Equal([HAlign.Left, HAlign.Center], alignments[2]);
    }

    [Fact]
    public void A_grid_tables_alignment_follows_the_cells_columns_past_a_span()
    {
        // A grid table's alignment is in its first line.
        const string Grid = """
            +:----+:---:+----:+
            | A   | B   | C   |
            +-----+-----+-----+
            | wide      | r   |
            +-----------+-----+
            """;

        var document = MarkdownReader.Read(Grid, fx.NewFolder());
        var table = document.Sections.Single().Blocks.OfType<HTable>().Single();
        var alignments = CellAlignments(document);

        Assert.Equal([HAlign.Left, HAlign.Center, HAlign.Right], alignments[0]);
        Assert.Equal(2, table.Rows[1].Cells[0].ColSpan);
        Assert.Equal([HAlign.Left, HAlign.Right], alignments[1]); // the cell after the span is in the third column
    }

    [Fact]
    public void Column_alignment_reaches_docx_hwpx_and_odt()
    {
        var document = MarkdownReader.Read(AlignedTable, fx.NewFolder());
        var folder = fx.NewFolder();
        var docx = Path.Combine(folder, "table.docx");
        var hwpx = Path.Combine(folder, "table.hwpx");
        var odt = Path.Combine(folder, "table.odt");
        DocxWriter.Write(document, docx);
        HwpxWriter.Write(document, hwpx);
        Filee.Engines.Hwp.Hwpx.Odt.OdtWriter.Write(document, odt, TestContext.Current.CancellationToken);

        var media = fx.NewFolder();
        foreach (var (name, read) in new[]
        {
            ("docx", DocxReader.Read(docx, media)),
            ("hwpx", HwpxReader.Read(hwpx, media)),
            ("odt", Filee.Engines.Hwp.Hwpx.Odt.OdtReader.Read(odt, media)),
        })
        {
            foreach (var row in CellAlignments(read))
            {
                Assert.True(row[0] is null or HAlign.Left or HAlign.Justify, $"{name}: {row[0]}");
                Assert.True(row[1] == HAlign.Center, $"{name}: {row[1]}");
                if (row.Length > 2)
                {
                    Assert.True(row[2] == HAlign.Right, $"{name}: {row[2]}");
                    Assert.True(row[3] is null or HAlign.Left or HAlign.Justify, $"{name}: {row[3]}");
                }
            }
        }
    }

    [Fact]
    public void Column_alignment_moves_the_text_in_the_rendered_cells()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        using var shaper = new Filee.Engines.Typesetting.Shaper();
        float X(string delimiter)
        {
            var document = MarkdownReader.Read($"| x | y |\n|---|{delimiter}|\n| a | b |\n", fx.NewFolder());
            var page = new Filee.Engines.Typesetting.PageComposer(new Filee.Engines.Typesetting.FontResolver(fonts), shaper)
                .Compose(document, TestContext.Current.CancellationToken).Pages[0];
            return page.Items.OfType<Filee.Engines.Rendering.GlyphItem>().Single(i => i.Run.Text.Trim() == "b").Run.Positions[0].X;
        }

        var plain = X("---");
        var centre = X(":-:");
        var right = X("--:");

        Assert.True(centre > plain + 50, $"plain {plain}, centred {centre}");
        Assert.True(right > centre + 50, $"centred {centre}, right {right}");
    }

    [Fact]
    public void The_markdown_writer_writes_the_column_alignment_back()
    {
        var markdown = MarkdownWriter.Write(MarkdownReader.Read(AlignedTable, fx.NewFolder()), _ => null);

        // Left is how a column shows anyway: it stays "---".
        Assert.Contains("| Left | Centre | Right | Plain |\n|---|:---:|---:|---|\n| a | b | c | d |\n| e | f |  |  |", markdown);
        var again = CellAlignments(MarkdownReader.Read(markdown, fx.NewFolder()));
        Assert.Equal([null, HAlign.Center, HAlign.Right, null], again[1]);
    }

    [Fact]
    public void A_column_whose_cells_differ_or_span_keeps_a_plain_delimiter()
    {
        var table = new HTable { ColumnCount = 2 };
        foreach (var (first, second) in new (HAlign? First, HAlign? Second)[] { (HAlign.Center, HAlign.Right), (HAlign.Right, HAlign.Right) })
        {
            var row = new HRow();
            foreach (var align in new[] { first, second })
            {
                var cell = new HCell();
                cell.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: align), Inlines = { new HText("t", default) } });
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        var spanning = new HCell { ColSpan = 2 };
        spanning.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Center), Inlines = { new HText("wide", default) } });
        table.Rows.Add(new HRow { Cells = { spanning } });
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { table } });

        Assert.Contains("|---|---:|\n", MarkdownWriter.Write(document, _ => null));

        // Right to left, Left is the start side: the right edge.
        foreach (var paragraph in table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).OfType<HParagraph>())
            paragraph.Format = new HParaFormat(Align: HAlign.Left, RightToLeft: true);
        Assert.Contains("|---:|---:|\n", MarkdownWriter.Write(document, _ => null));
    }
}
